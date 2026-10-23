using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// A/B 实验编排器（ExperimentRunner，取代 tuning-experiment.ps1）。
/// 行为契约与旧脚本测试一一对应：隔离状态目录、顺序闸门在写盘前拒绝、
/// keep/reverted/revertError/message 四字段自洽（"绝不谎报已还原"的可测形式）。
/// 只跑模拟模式与纯解析函数，不触碰真实系统。
/// </summary>
[Collection("BackupService serial")]
public class ExperimentRunnerTests
{
    [Fact]
    public void Bounded_frame_parser_rejects_partial_results_and_observes_cancellation_during_enumeration()
    {
        var lines = new[] { "FrameTime" }.Concat(Enumerable.Repeat("8", 31));
        var limited = FrameTimeStats.Parse(lines, CancellationToken.None, 30);
        Assert.Null(limited.Frame); Assert.Equal(FrameParseError.TooManyFrames, limited.Error); Assert.Equal(31, limited.FrameCount);
        Assert.Equal(30, FrameTimeStats.Parse(lines.Take(31), CancellationToken.None, 30).Frame!.Samples);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> Interrupted()
        {
            yield return "FrameTime";
            for (var i = 0; i < 100; i++) { if (i == 35) cancellation.Cancel(); yield return "8"; }
        }
        Assert.Throws<OperationCanceledException>(() => FrameTimeStats.Parse(Interrupted(), cancellation.Token, 100));
    }

    [Theory]
    [InlineData("\n")] [InlineData("\r\n")] [InlineData("\r")]
    public void Streaming_csv_preserves_bom_quoted_fields_and_all_line_endings(string newline)
    {
        using var scope = TempStateDir.Create(); var path = Path.Combine(scope.Dir, "quoted.csv");
        File.WriteAllText(path, string.Join(newline, new[] { "\"App,name\",\"FrameTime\"" }.Concat(Enumerable.Repeat("\"game,edition\",8", 40))), Encoding.Unicode);
        var result = ExperimentRunner.ParsePresentMonCsv(path)!;
        Assert.Equal(40, result["samples"]!.GetValue<int>()); Assert.Equal(125, result["avgFps"]!.GetValue<double>());
    }

    [Fact]
    public async Task Invalid_csv_is_rejected_before_experiment_mutation_and_preserves_existing_state()
    {
        using var scope = TempStateDir.Create(); var state = Path.Combine(scope.Dir, "state-v2.json");
        var history = Path.Combine(scope.Dir, "history-v2.jsonl"); File.WriteAllText(state, "{\"original\":true}"); File.WriteAllText(history, "original history");
        var large = Path.Combine(scope.Dir, "large.csv"); using (var file = File.Create(large)) file.SetLength(ExperimentRunner.MaxCsvInputBytes + 1);
        var wide = Path.Combine(scope.Dir, "wide.csv"); File.WriteAllText(wide, "FrameTime\n" + string.Concat(Enumerable.Repeat("8\n", 40)) + new string('x', FrameTimeStats.MaxCsvLineChars + 1));
        var lockedPath = Path.Combine(scope.Dir, "locked.csv"); File.WriteAllText(lockedPath, "FrameTime\n8\n");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var writes = 0; FpsTune.Wpf.Core.RegistryHelper.SetOverride = (_, _, _, _, _) => { writes++; throw new Exception("Unexpected mutation"); };
        try
        {
            foreach (var (path, key) in new[] { (large, "Str.CsvInputTooLarge"), (wide, "Str.CsvInputLineTooLong"), (lockedPath, "Str.CsvInputReadFailed") })
            {
                var result = await ExperimentRunner.RunAsync("custom", new(CsvPath: path, Items: ["transparency-off"]), CancellationToken.None);
                Assert.Equal(1, result.ExitCode); Assert.Equal(Str.T(key), ParseJson(result.Json).GetProperty("error").GetString());
                Assert.Equal("{\"original\":true}", File.ReadAllText(state)); Assert.Equal("original history", File.ReadAllText(history));
            }
            Assert.Equal(0, writes); Assert.False(File.Exists(Path.Combine(scope.Dir, "in-flight-v2.json")));
        }
        finally { FpsTune.Wpf.Core.RegistryHelper.SetOverride = null; }
    }

    private static readonly ExperimentRunner.Options Simulate = new(Simulate: true);

    [Fact]
    public async Task Baseline_json_succeeds_in_isolated_state_dir()
    {
        using var scope = TempStateDir.Create();
        var result = await ExperimentRunner.RunAsync("baseline", Simulate, CancellationToken.None);
        var json = ParseJson(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("baseline", json.GetProperty("mode").GetString());
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var expectedVersion = props.Descendants("VersionPrefix").First().Value.Trim();
        Assert.Equal(expectedVersion, json.GetProperty("version").GetString());
        Assert.True(File.Exists(Path.Combine(scope.Dir, "state-simulated-v2.json")));
        Assert.True(File.Exists(Path.Combine(scope.Dir, "history-simulated-v2.jsonl")));
    }

    [Fact]
    public async Task Group_two_before_group_one_fails_without_writing_state_history_or_csv()
    {
        using var scope = TempStateDir.Create();
        Assert.Equal(0, (await ExperimentRunner.RunAsync("baseline", Simulate, CancellationToken.None)).ExitCode);

        var stateBefore = File.ReadAllBytes(Path.Combine(scope.Dir, "state-simulated-v2.json"));
        var historyBefore = File.ReadAllBytes(Path.Combine(scope.Dir, "history-simulated-v2.jsonl"));

        var result = await ExperimentRunner.RunAsync("group-2", Simulate, CancellationToken.None);
        var json = ParseJson(result.Json);

        Assert.Equal(1, result.ExitCode);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("group-1", json.GetProperty("error").GetString());
        Assert.Equal(stateBefore, File.ReadAllBytes(Path.Combine(scope.Dir, "state-simulated-v2.json")));
        Assert.Equal(historyBefore, File.ReadAllBytes(Path.Combine(scope.Dir, "history-simulated-v2.jsonl")));
        Assert.False(File.Exists(Path.Combine(scope.Dir, "experiment-summary-simulated.csv")));
    }

    [Fact]
    public async Task Group_one_without_baseline_returns_json_failure_and_exit_one()
    {
        using var scope = TempStateDir.Create();
        var result = await ExperimentRunner.RunAsync("group-1", Simulate, CancellationToken.None);
        var json = ParseJson(result.Json);

        Assert.Equal(1, result.ExitCode);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("基线", json.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(scope.Dir, "state-simulated-v2.json")));
        Assert.False(File.Exists(Path.Combine(scope.Dir, "history-simulated-v2.jsonl")));
    }

    [Fact]
    public async Task Simulated_group_test_reports_revert_honestly_and_persists_it()
    {
        using var scope = TempStateDir.Create();
        Assert.Equal(0, (await ExperimentRunner.RunAsync("baseline", Simulate, CancellationToken.None)).ExitCode);
        Assert.Equal(0, (await ExperimentRunner.RunAsync("group-1", Simulate, CancellationToken.None)).ExitCode);

        var result = await ExperimentRunner.RunAsync("group-2", Simulate, CancellationToken.None);
        var json = ParseJson(result.Json);

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
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(scope.Dir, "state-simulated-v2.json")));
        var group = state.RootElement.GetProperty("groups").EnumerateArray()
            .First(g => g.GetProperty("id").GetString() == "group-2");
        Assert.Equal(keep, group.GetProperty("keep").GetBoolean());
        Assert.Equal(reverted, group.GetProperty("reverted").GetBoolean());
        Assert.Equal(revertError, group.GetProperty("revertError").GetString());
    }

    [Fact]
    public async Task Report_after_full_simulated_chain_exports_csv_with_all_groups()
    {
        using var scope = TempStateDir.Create();
        Assert.Equal(0, (await ExperimentRunner.RunAsync("baseline", Simulate, CancellationToken.None)).ExitCode);
        foreach (var group in new[] { "group-1", "group-2", "group-3" })
            Assert.Equal(0, (await ExperimentRunner.RunAsync(group, Simulate, CancellationToken.None)).ExitCode);

        var result = await ExperimentRunner.RunAsync("report", Simulate, CancellationToken.None);
        var json = ParseJson(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.True(json.GetProperty("ok").GetBoolean());
        var groups = json.GetProperty("groups");
        foreach (var id in new[] { "group-1", "group-2", "group-3" })
            Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("id").GetString() == id);
        Assert.True(File.Exists(json.GetProperty("csvExport").GetString()));
    }

    [Fact]
    public async Task Manual_single_csv_is_one_sample_and_never_fake_stable()
    {
        // 旧脚本把手动模式同一份 CSV 当 3 次样本算出 CV=0 的假"稳定"；
        // 现在如实算单次样本且不判定稳定（sampleCount < 3 → stable=false）。
        using var scope = TempStateDir.Create();
        var csv = Path.Combine(scope.Dir, "manual.csv");
        File.WriteAllLines(csv, SyntheticCsv(40, frameMs: 10.0));
        var options = new ExperimentRunner.Options(CsvPath: csv);

        var result = await ExperimentRunner.RunAsync("baseline", options, CancellationToken.None);
        var json = ParseJson(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("manual", json.GetProperty("samplerMode").GetString());
        var summary = json.GetProperty("baseline").GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("sampleCount").GetInt32());
        Assert.False(summary.GetProperty("stable").GetBoolean());
        Assert.Equal(100.0, summary.GetProperty("avgFps").GetDouble());
    }

    [Fact]
    public void PresentMon_csv_v2_columns_parse_and_short_samples_are_rejected()
    {
        using var scope1 = TempStateDir.Create();
        var csv = Path.Combine(scope1.Dir, "sample.csv");
        File.WriteAllLines(csv, SyntheticCsv(40, frameMs: 8.0));
        var stat = ExperimentRunner.ParsePresentMonCsv(csv)!;

        Assert.False(stat.ContainsKey("error"));
        Assert.Equal(40, stat["samples"]!.GetValue<int>());
        Assert.Equal(125.0, stat["avgFps"]!.GetValue<double>());
        Assert.Equal(0, stat["stutters"]!.GetValue<int>());

        using var scope2 = TempStateDir.Create();
        var shortCsv = Path.Combine(scope2.Dir, "short.csv");
        File.WriteAllLines(shortCsv, SyntheticCsv(10, frameMs: 8.0));
        var failed = ExperimentRunner.ParsePresentMonCsv(shortCsv)!;
        Assert.True(failed.ContainsKey("error"));
    }

    [Fact]
    public async Task Changing_conditions_rejects_group_and_new_baseline_clears_old_groups()
    {
        using var scope = TempStateDir.Create();
        await ExperimentRunner.RunAsync("baseline", Simulate, CancellationToken.None);
        await ExperimentRunner.RunAsync("group-1", Simulate, CancellationToken.None);
        var changed = await ExperimentRunner.RunAsync("custom", Simulate with { DurationSec = 60, Items = ["transparency-off"] }, CancellationToken.None);
        Assert.Equal(1, changed.ExitCode);
        await ExperimentRunner.RunAsync("baseline", Simulate, CancellationToken.None);
        var report = ParseJson((await ExperimentRunner.RunAsync("report", Simulate, CancellationToken.None)).Json);
        Assert.Empty(report.GetProperty("groups").EnumerateArray());
        Assert.False(File.Exists(Path.Combine(scope.Dir, "state-v2.json")));
        Assert.False(File.Exists(Path.Combine(scope.Dir, "history.jsonl")));
    }

    [Fact]
    public void Tail_latency_or_unstable_samples_cannot_be_accepted_for_average_gain()
    {
        static System.Text.Json.Nodes.JsonObject Summary(double p99, bool stable = true) => new()
        { ["avgFps"] = 110.0, ["p1Low"] = 60.0, ["p99Ms"] = p99, ["stutters"] = 0, ["stable"] = stable };
        Assert.False(ExperimentRunner.DecideKeep(Summary(20), Summary(21)).Keep);
        Assert.False(ExperimentRunner.DecideKeep(Summary(20), Summary(19, false)).Keep);
        Assert.False(ExperimentRunner.DecideKeep(Summary(20), Summary(double.NaN)).Keep);
    }

    [Fact]
    public void Unsigned_or_unapproved_capture_tool_is_rejected_without_execution()
    {
        using var scope = TempStateDir.Create();
        var path = Path.Combine(scope.Dir, "presentmon.exe");
        File.WriteAllText(path, "not an executable");
        Assert.False(TrustedCaptureTool.IsTrusted(path));
        Assert.True(TrustedCaptureTool.IsAllowedPublisher("Intel Corporation"));
        Assert.False(TrustedCaptureTool.IsAllowedPublisher("Intel Corporation unofficial"));
    }

    // ---------- 工具 ----------

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Directory.Build.props")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!;
        }
        throw new InvalidOperationException("未定位到仓库根");
    }

    private static JsonElement ParseJson(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    /// <summary>生成合成 PresentMon CSV（v2 列名 msBetweenPresents 在第 3 列）。</summary>
    private static IEnumerable<string> SyntheticCsv(int rows, double frameMs)
    {
        yield return "PresentMon,v2,msBetweenPresents";
        for (var i = 0; i < rows; i++)
            yield return $"0,0,{frameMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>隔离的实验状态目录；用完即删。挂在本类的串行 Collection 下防静态注入互踩。</summary>
    private sealed class TempStateDir : IDisposable
    {
        public string Dir { get; }

        private TempStateDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "fpstune-exptest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            ExperimentRunner.StateDirOverride = Dir;
        }

        public static TempStateDir Create() => new();

        public void Dispose()
        {
            if (ExperimentRunner.StateDirOverride == Dir)
                ExperimentRunner.StateDirOverride = null;
            if (Directory.Exists(Dir))
                Directory.Delete(Dir, recursive: true);
        }
    }
}
