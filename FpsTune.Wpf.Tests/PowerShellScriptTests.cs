using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class PowerShellScriptTests
{
    [Fact]
    public async Task Baseline_json_succeeds_in_isolated_local_app_data()
    {
        var dataRoot = NewDataRoot();
        try
        {
            var result = await RunScriptAsync(dataRoot, "-Baseline", "-Simulate", "-Json");
            var json = ParseJson(result.Stdout);

            Assert.Equal(0, result.ExitCode);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal("baseline", json.GetProperty("mode").GetString());
            var props = System.Xml.Linq.XDocument.Load(Path.Combine(FindRepositoryRoot(), "Directory.Build.props"));
            var expectedVersion = props.Descendants("Version").First().Value.Trim();
            Assert.Equal(expectedVersion, json.GetProperty("version").GetString());
            Assert.True(File.Exists(Path.Combine(dataRoot, "FpsTune", "experiment", "state.json")));
            Assert.True(File.Exists(Path.Combine(dataRoot, "FpsTune", "experiment", "history.jsonl")));
        }
        finally
        {
            DeleteDataRoot(dataRoot);
        }
    }

    [Fact]
    public async Task Group_two_before_group_one_fails_without_writing_state_history_or_csv()
    {
        var dataRoot = NewDataRoot();
        try
        {
            var baseline = await RunScriptAsync(dataRoot, "-Baseline", "-Simulate", "-Json");
            Assert.Equal(0, baseline.ExitCode);

            var experimentDir = Path.Combine(dataRoot, "FpsTune", "experiment");
            var statePath = Path.Combine(experimentDir, "state.json");
            var historyPath = Path.Combine(experimentDir, "history.jsonl");
            var csvPath = Path.Combine(experimentDir, "experiment-summary.csv");
            var stateBefore = File.ReadAllBytes(statePath);
            var historyBefore = File.ReadAllBytes(historyPath);

            var result = await RunScriptAsync(dataRoot, "-Test", "-Group", "group-2", "-Simulate", "-Json");
            var json = ParseJson(result.Stdout);

            Assert.Equal(1, result.ExitCode);
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.Contains("group-1", json.GetProperty("error").GetString());
            Assert.Equal(stateBefore, File.ReadAllBytes(statePath));
            Assert.Equal(historyBefore, File.ReadAllBytes(historyPath));
            Assert.False(File.Exists(csvPath));
        }
        finally
        {
            DeleteDataRoot(dataRoot);
        }
    }

    [Fact]
    public async Task Group_one_without_baseline_returns_json_failure_and_exit_one()
    {
        var dataRoot = NewDataRoot();
        try
        {
            var result = await RunScriptAsync(dataRoot, "-Test", "-Group", "group-1", "-Simulate", "-Json");
            var json = ParseJson(result.Stdout);

            Assert.Equal(1, result.ExitCode);
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.Contains("Baseline", json.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(dataRoot, "FpsTune")));
        }
        finally
        {
            DeleteDataRoot(dataRoot);
        }
    }

    [Fact]
    public async Task Simulated_group_test_reports_revert_honestly_and_persists_it()
    {
        var dataRoot = NewDataRoot();
        try
        {
            Assert.Equal(0, (await RunScriptAsync(dataRoot, "-Baseline", "-Simulate", "-Json")).ExitCode);
            Assert.Equal(0, (await RunScriptAsync(dataRoot, "-Test", "-Group", "group-1", "-Simulate", "-Json")).ExitCode);

            var result = await RunScriptAsync(dataRoot, "-Test", "-Group", "group-2", "-Simulate", "-Json");
            var json = ParseJson(result.Stdout);

            Assert.Equal(0, result.ExitCode);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal("simulated", json.GetProperty("samplerMode").GetString());

            // 不赌模拟随机数落在哪一侧：只要求 keep / reverted / revertError / message
            // 四个字段彼此自洽——这正是"绝不谎报已还原"的可测形式。
            var keep = json.GetProperty("keep").GetBoolean();
            var reverted = json.GetProperty("reverted").GetBoolean();
            var revertError = json.GetProperty("revertError").GetString() ?? "";
            var message = json.GetProperty("message").GetString() ?? "";
            if (keep)
            {
                Assert.False(reverted);
                Assert.Equal("", revertError);
                Assert.Contains("保留", message, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(reverted, "判定为无收益却没报告已还原：" + message);
                Assert.Equal("", revertError);
                Assert.Contains("已还原", message, StringComparison.Ordinal);
            }

            // 同一组结论必须原样落到 state.json，且字段齐全（历史/报告都靠它）
            using var state = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(dataRoot, "FpsTune", "experiment", "state.json")));
            var group = state.RootElement.GetProperty("groups").EnumerateArray()
                .First(g => g.GetProperty("id").GetString() == "group-2");
            Assert.Equal(keep, group.GetProperty("keep").GetBoolean());
            Assert.Equal(reverted, group.GetProperty("reverted").GetBoolean());
            Assert.Equal(revertError, group.GetProperty("revertError").GetString());
        }
        finally
        {
            DeleteDataRoot(dataRoot);
        }
    }

    private static string NewDataRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "fpstune-powershell-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDataRoot(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static JsonElement ParseJson(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private static async Task<ScriptRun> RunScriptAsync(string dataRoot, params string[] arguments)
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "tuning-experiment.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["LOCALAPPDATA"] = dataRoot;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 powershell.exe");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptRun(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // 构建输出也可能复制脚本；只有同时存在版本单一来源时才是仓库根目录。
            if (File.Exists(Path.Combine(directory.FullName, "tuning-experiment.ps1"))
                && File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法从测试输出目录定位 tuning-experiment.ps1");
    }

    private sealed record ScriptRun(int ExitCode, string Stdout, string Stderr);
}
