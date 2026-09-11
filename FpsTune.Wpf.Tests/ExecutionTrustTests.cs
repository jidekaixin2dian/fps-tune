using System.Diagnostics;
using System.Security.Cryptography;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// P1：脚本执行路径的信任边界。
/// - 提权会话不得执行"与内置版本不一致"的脚本（应用目录里的同名文件可被同账户进程改写）；
/// - 校验通过的内容必须在执行期被锁住，且由真正执行的子进程再校验一次哈希；
/// - 启动器不得把用户数据写进临时文件或 Windows 命令行。
/// </summary>
public sealed class ExecutionTrustTests
{
    [Fact]
    public void Tampered_extraction_is_replaced_and_locked_while_running()
    {
        var root = NewDir();
        var app = Path.Combine(root, "app");
        var extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extracted);
        const string name = "friend-test.ps1";
        var target = Path.Combine(extracted, name);
        File.WriteAllText(target, "throw 'tampered'");
        try
        {
            using var script = ScriptLocator.OpenVerified(name, app, extracted, elevated: false);

            Assert.Equal(target, script.Path);
            Assert.Equal(Embedded(name), File.ReadAllBytes(script.Path));
            Assert.Equal(HashOf(Embedded(name)), script.Sha256);

            // 执行期间：写与删都必须被拒绝（否则"校验通过"没有意义）。
            Assert.Throws<IOException>(() => File.WriteAllText(script.Path, "throw 'rewritten'"));
            var deleted = false;
            try { File.Delete(script.Path); } catch (IOException) { deleted = true; }
            catch (UnauthorizedAccessException) { deleted = true; }
            Assert.False(deleted, "执行期间脚本文件不得被替换或删除");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Tampered_app_directory_script_is_refused_in_elevated_session()
    {
        var root = NewDir();
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        const string name = "tuning-experiment.ps1";
        var tampered = Path.Combine(app, name);
        File.WriteAllText(tampered, "throw 'tampered'");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                using var script = ScriptLocator.OpenVerified(
                    name, app, Path.Combine(root, "extracted"), elevated: true);
            });

            Assert.Contains("拒绝在管理员会话中执行", ex.Message, StringComparison.Ordinal);
            Assert.Equal("throw 'tampered'", File.ReadAllText(tampered)); // 只拒绝执行，不改写用户文件
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Matching_app_directory_script_is_accepted_in_elevated_session()
    {
        var root = NewDir();
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        const string name = "tuning-experiment.ps1";
        var scriptPath = Path.Combine(app, name);
        File.WriteAllBytes(scriptPath, Embedded(name));
        try
        {
            using var script = ScriptLocator.OpenVerified(
                name, app, Path.Combine(root, "extracted"), elevated: true);

            Assert.Equal(scriptPath, script.Path);
            // 提权会话登记的一定是"内置版本"的哈希，而不是临时读到的值。
            Assert.Equal(HashOf(Embedded(name)), script.Sha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Development_script_is_allowed_when_not_elevated()
    {
        var root = NewDir();
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        const string name = "friend-test.ps1";
        var scriptPath = Path.Combine(app, name);
        File.WriteAllText(scriptPath, "Write-Host 'dev copy'");
        try
        {
            using var script = ScriptLocator.OpenVerified(
                name, app, Path.Combine(root, "extracted"), elevated: false);

            Assert.Equal(scriptPath, script.Path);
            Assert.Equal("Write-Host 'dev copy'", File.ReadAllText(script.Path));
            // 非提权会话没有可信基准，但至少登记"实际内容"的哈希供子进程复校验。
            Assert.Equal(HashOf(File.ReadAllBytes(script.Path)), script.Sha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unknown_script_names_are_still_rejected()
    {
        var root = NewDir();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                ScriptLocator.OpenVerified("evil.ps1", root, root, elevated: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Launch_command_is_constant_and_data_travels_via_environment()
    {
        var script = Path.Combine(Path.GetTempPath(), "fpstune scripts", "friend-test.ps1");
        var args = new[] { "-Name", "张三 'quoted' $(not-a-command)", "-Scene", "靶场，2K 全高" };
        var expected = new string('A', 64);
        var psi = new ProcessStartInfo();

        PowerShellRunner.ApplyLaunchEnvironment(psi, script, args, expected);

        Assert.Equal(Path.GetFullPath(script), psi.Environment[PowerShellRunner.ScriptPathVariable]);
        Assert.Equal(expected, psi.Environment[PowerShellRunner.ScriptHashVariable]);
        Assert.Equal(
            args.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            psi.Environment[PowerShellRunner.ArgCountVariable]);
        for (var i = 0; i < args.Length; i++)
            Assert.Equal(args[i], psi.Environment[PowerShellRunner.ArgVariablePrefix + i]);

        // 启动命令是常量：脚本路径、参数与哈希一个字符都不参与命令行（也不落盘）；
        // 但必须包含"由子进程自己校验哈希"这一步。
        Assert.Contains("Get-FileHash", PowerShellRunner.LaunchCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(script), PowerShellRunner.LaunchCommand, StringComparison.Ordinal);
        foreach (var arg in args)
            Assert.DoesNotContain(arg, PowerShellRunner.LaunchCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_environment_falls_back_to_local_hash_when_no_trusted_reference()
    {
        var root = NewDir();
        var script = Path.Combine(root, "echo.ps1");
        File.WriteAllText(script, "[Console]::WriteLine('ok')");
        try
        {
            var psi = new ProcessStartInfo();
            PowerShellRunner.ApplyLaunchEnvironment(psi, script, Array.Empty<string>(), expectedSha256: null);

            Assert.Equal(HashOf(File.ReadAllBytes(script)), psi.Environment[PowerShellRunner.ScriptHashVariable]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Launch_environment_refuses_unreadable_script()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fpstune-missing-" + Guid.NewGuid().ToString("N") + ".ps1");
        var psi = new ProcessStartInfo();

        Assert.Throws<InvalidOperationException>(() =>
            PowerShellRunner.ApplyLaunchEnvironment(psi, missing, Array.Empty<string>(), expectedSha256: null));
    }

    [Fact]
    public void Oversized_launch_payload_is_rejected_instead_of_written_to_disk()
    {
        var psi = new ProcessStartInfo();
        var script = Path.Combine(Path.GetTempPath(), "friend-test.ps1");
        var huge = new string('x', PowerShellRunner.MaxLaunchPayloadChars + 1);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PowerShellRunner.ApplyLaunchEnvironment(psi, script, new[] { "-Notes", huge }, new string('A', 64)));

        Assert.Contains("参数过长", ex.Message, StringComparison.Ordinal);
        Assert.False(
            psi.Environment.ContainsKey(PowerShellRunner.ScriptPathVariable),
            "被拒绝时不得留下半套环境变量");
    }

    /// <summary>端到端：哈希对不上时，子进程必须拒绝执行（而不是照跑）。Windows CI 覆盖。</summary>
    [Fact]
    public async Task Child_refuses_to_execute_when_hash_does_not_match()
    {
        var root = NewDir();
        var script = Path.Combine(root, "echo.ps1");
        File.WriteAllText(script, "[Console]::WriteLine('should-not-run')" + Environment.NewLine + "exit 0");
        try
        {
            var result = await PowerShellRunner.RunAsync(script, Array.Empty<string>(), new string('0', 64));

            Assert.Equal(PowerShellRunner.ScriptIntegrityExitCode, result.ExitCode);
            Assert.DoesNotContain("should-not-run", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string HashOf(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content));

    private static byte[] Embedded(string name)
    {
        using var stream = typeof(ScriptLocator).Assembly.GetManifestResourceStream("FpsTune.Wpf.Scripts." + name);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream!.CopyTo(memory);
        return memory.ToArray();
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpstune-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
