using System.Text.Json;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// P1：A/B 实验的自动还原必须锚定"本步骤自己写下的那份快照"。
/// 拿不到锚定文件时必须明确失败，绝不能回退去动更早的备份——
/// 那等于把别的步骤/别的日期的原值当成自己的原值写回系统。
/// </summary>
[Collection("BackupService serial")]
public sealed class BackupAnchorTests
{
    [Fact]
    public void Anchored_restore_only_consumes_the_given_backup_file()
    {
        var dir = NewDir();
        BackupService.BackupDirOverride = dir;
        var restored = new List<string>();
        BackupService.RestoreRecordOverride = record => restored.Add(record.Id);
        try
        {
            var older = BackupService.Capture(new[] { "transparency-off" }, null);
            var newer = BackupService.Capture(new[] { "transparency-off" }, null);

            var result = BackupService.RestoreAll(new[] { "transparency-off" }, newer);

            Assert.Empty(result.Failures);
            Assert.Single(result.Restored);
            Assert.Single(restored);
            Assert.True(File.Exists(newer + ".restored"), "本步骤的快照应被消费");
            Assert.True(File.Exists(older), "更早的备份绝不能被本步骤动到");

            // 二次锚定同一份快照：文件已被消费，必须明确报错，而不是转去动更早的备份。
            var again = BackupService.RestoreAll(new[] { "transparency-off" }, newer);
            Assert.Empty(again.Restored);
            Assert.Single(restored);
            Assert.Contains(again.Failures, x => x.Contains("不存在", StringComparison.Ordinal));
            Assert.True(File.Exists(older), "失败的锚定不得降级成「还原全部」");
        }
        finally
        {
            Reset(dir);
        }
    }

    [Fact]
    public void Anchored_restore_rejects_paths_outside_the_backup_directory()
    {
        var dir = NewDir();
        var outside = NewDir();
        var foreign = Path.Combine(outside, "csharp-backup-20260101-000000-000-00000000.json");
        File.WriteAllText(foreign, "[]");

        BackupService.BackupDirOverride = dir;
        var calls = 0;
        BackupService.RestoreRecordOverride = _ => calls++;
        try
        {
            var outsideResult = BackupService.RestoreAll(null, foreign);
            Assert.Empty(outsideResult.Restored);
            Assert.Contains(outsideResult.Failures, x => x.Contains("不合法", StringComparison.Ordinal));

            var relativeResult = BackupService.RestoreAll(null, "csharp-backup-20260101-000000-000-00000000.json");
            Assert.Empty(relativeResult.Restored);
            Assert.Contains(relativeResult.Failures, x => x.Contains("不合法", StringComparison.Ordinal));

            Assert.Equal(0, calls);
        }
        finally
        {
            Reset(dir);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Cli_restore_with_backup_file_restores_only_that_snapshot()
    {
        var dir = NewDir();
        BackupService.BackupDirOverride = dir;
        var restored = new List<string>();
        BackupService.RestoreRecordOverride = record => restored.Add(record.Id);
        try
        {
            var older = BackupService.Capture(new[] { "transparency-off" }, null);
            var newer = BackupService.Capture(new[] { "transparency-off" }, null);

            var writer = new StringWriter();
            var exitCode = CliHost.Run(
                new[] { "-Restore", "-Items", "transparency-off", "-BackupFile", newer, "-Json" }, writer);

            Assert.Equal(0, exitCode);
            var payload = JsonDocument.Parse(writer.ToString());
            Assert.Equal(1, payload.RootElement.GetProperty("restored").GetArrayLength());
            Assert.Single(restored);
            Assert.True(File.Exists(newer + ".restored"));
            Assert.True(File.Exists(older));
        }
        finally
        {
            Reset(dir);
        }
    }

    [Fact]
    public void Cli_restore_with_unknown_backup_file_fails_instead_of_falling_back()
    {
        var dir = NewDir();
        BackupService.BackupDirOverride = dir;
        var calls = 0;
        BackupService.RestoreRecordOverride = _ => calls++;
        try
        {
            BackupService.Capture(new[] { "transparency-off" }, null);

            var writer = new StringWriter();
            var exitCode = CliHost.Run(
                new[] { "-Restore", "-Items", "transparency-off", "-BackupFile", Path.Combine(dir, "csharp-backup-19990101-000000-000-00000000.json"), "-Json" },
                writer);

            Assert.Equal(1, exitCode);
            // -Json 输出对非 ASCII 会做 \uXXXX 转义，必须按消费者的方式解析后再断言，
            // 不能直接比对中文字面量（CLI 的调用方也是先 ConvertFrom-Json）。
            using var payload = JsonDocument.Parse(writer.ToString());
            var failures = payload.RootElement.GetProperty("failures")
                .EnumerateArray()
                .Select(x => x.GetString() ?? string.Empty)
                .ToList();
            Assert.Contains(failures, x => x.Contains("不存在", StringComparison.Ordinal));
            Assert.Equal(0, calls);
        }
        finally
        {
            Reset(dir);
        }
    }

    [Fact]
    public void Cli_rejects_backup_file_for_other_verbs()
    {
        var writer = new StringWriter();
        var exitCode = CliHost.Run(new[] { "-Detect", "-BackupFile", "x.json", "-Json" }, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("参数错误", writer.ToString(), StringComparison.Ordinal);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpstune-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Reset(string dir)
    {
        BackupService.RestoreRecordOverride = null;
        BackupService.BackupDirOverride = null;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
