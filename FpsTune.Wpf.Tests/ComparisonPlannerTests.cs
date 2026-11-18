using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>同步 IProgress：Progress&lt;T&gt; 的回调经线程池异步派发，断言需要确定性的顺序与完成时机。</summary>
file sealed class SyncProgress(Action<ComparisonStage> action) : IProgress<ComparisonStage>
{
    public void Report(ComparisonStage stage) => action(stage);
}

/// <summary>
/// ComparisonPlanner：状态机正常路径、各中断路径（改前失败 / 应用失败 / 无备份快照 /
/// 改后失败自动还原 / 还原失败如实报告）与回滚接线。采样、应用、还原、冷却全部注入。
/// UserDataPaths.RootOverride 是全局静态：与其他设置它的测试类共享串行 collection。
/// </summary>
[Collection("BackupService serial")]
public sealed class ComparisonPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fpstune-compare-tests-" + Guid.NewGuid().ToString("N"));

    public ComparisonPlannerTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        UserDataPaths.RootOverride = null;
        ComparisonPlanner.CaptureOverride = null;
        ComparisonPlanner.ApplyOverride = null;
        ComparisonPlanner.RestoreOverride = null;
        ComparisonPlanner.CooldownOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// 写一份逐帧 CSV：均值 ms。每 600 帧加逐段偏移——段均值各不相同，
    /// 否则恒定帧时间会切出五元组完全相同的段（被 SampleStatistics 重复段闸判为证据不足）。
    /// </summary>
    private string WriteFrameCsv(string name, int frameCount = 3600, double ms = 1000.0 / 60)
    {
        var path = Path.Combine(_root, name);
        var lines = new List<string> { "msBetweenPresents,msInPresentAPI" };
        for (var i = 0; i < frameCount; i++)
        {
            var bias = ((i / 600) % 6 - 2.5) * 0.0006;
            lines.Add((ms * (1 + bias)).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ",1.0");
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    private static CaptureRun SuccessfulRun(string csvPath) => new(
        "capture-test", DateTimeOffset.Now, 60, "comparison", 3600, 60, 37.5, 26.7, 12,
        null, null, csvPath, Path.ChangeExtension(csvPath, ".json"), null, null);

    private static CaptureRun FailedRun(string reasonCode) => new(
        "capture-test", DateTimeOffset.Now, 60, "comparison", 0, 0, 0, 0, 0,
        null, null, Path.Combine(Path.GetTempPath(), "frames.csv"),
        Path.Combine(Path.GetTempPath(), "run.json"), "采样失败文案", reasonCode);

    private static ComparisonRequest Request(params string[] items) =>
        new(items, GamePath: null, SampleSeconds: 60, CooldownSeconds: 0);

    [Fact]
    public async Task Happy_path_produces_verdict_and_rolls_back_nothing()
    {
        var beforeCsv = WriteFrameCsv("before.csv", ms: 1000.0 / 100); // 100 FPS
        var afterCsv = WriteFrameCsv("after.csv", ms: 1000.0 / 110);   // 110 FPS（+10%）
        var backup = Path.Combine(_root, "backup.json");
        File.WriteAllText(backup, "{}");
        var stages = new List<ComparisonStage>();
        var rollbackCalled = 0;
        var captureRuns = 0;
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(
            SuccessfulRun(++captureRuns == 1 ? beforeCsv : afterCsv));
        ComparisonPlanner.ApplyOverride = (_, _, _) => Task.FromResult(
            new ComparisonApplyOutcome(true, null, backup, ["sysmain"]));
        ComparisonPlanner.RestoreOverride = (_, _) => { rollbackCalled++; return new ComparisonRollbackResult(true, 1, null); };
        ComparisonPlanner.CooldownOverride = (_, _) => Task.CompletedTask;

        var result = await ComparisonPlanner.RunAsync(
            Request("sysmain"), CancellationToken.None, new SyncProgress(stages.Add));

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.NotNull(result.Verdict);
        Assert.Equal(GainDecision.Keep, result.Verdict!.Decision);
        Assert.Equal(["sysmain"], result.AppliedIds);
        Assert.Equal(backup, result.BackupFile);
        Assert.False(result.RolledBack);
        Assert.Equal(0, rollbackCalled);
        Assert.Equal(
            [ComparisonStage.Before, ComparisonStage.Apply, ComparisonStage.Cooldown, ComparisonStage.After, ComparisonStage.Verdict],
            stages);
    }

    [Fact]
    public async Task Before_capture_failure_aborts_without_applying()
    {
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(FailedRun("no-permission"));
        var applyCalled = false;
        ComparisonPlanner.ApplyOverride = (_, _, _) => { applyCalled = true; throw new InvalidOperationException("不应被调用"); };

        var result = await ComparisonPlanner.RunAsync(Request("sysmain"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("no-permission", result.ReasonCode);
        Assert.Contains("管理员", result.Error);
        Assert.Empty(result.AppliedIds);
        Assert.Null(result.BackupFile);
        Assert.False(applyCalled);
    }

    [Fact]
    public async Task Apply_failure_aborts()
    {
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(SuccessfulRun(WriteFrameCsv("b.csv")));
        ComparisonPlanner.ApplyOverride = (_, _, _) =>
            Task.FromResult(new ComparisonApplyOutcome(false, "sysmain — 拒绝访问", null, []));

        var result = await ComparisonPlanner.RunAsync(Request("sysmain"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("apply-failed", result.ReasonCode);
        Assert.Contains("sysmain", result.Error);
        Assert.Empty(result.AppliedIds);
    }

    [Fact]
    public async Task Apply_without_backup_snapshot_aborts()
    {
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(SuccessfulRun(WriteFrameCsv("b2.csv")));
        ComparisonPlanner.ApplyOverride = (_, _, _) =>
            Task.FromResult(new ComparisonApplyOutcome(true, null, null, ["sysmain"]));

        var result = await ComparisonPlanner.RunAsync(Request("sysmain"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("no-backup", result.ReasonCode);
        Assert.Contains("备份", result.Error);
    }

    [Fact]
    public async Task Partial_apply_failure_rolls_back_only_its_changed_items()
    {
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(SuccessfulRun(WriteFrameCsv("partial.csv")));
        var backup = Path.Combine(_root, "partial-backup.json");
        File.WriteAllText(backup, "{}");
        ComparisonPlanner.ApplyOverride = (_, _, _) =>
            Task.FromResult(new ComparisonApplyOutcome(false, "error", backup, ["sysmain"]));
        ComparisonPlanner.RestoreOverride = (file, ids) =>
        {
            Assert.Equal(backup, file);
            Assert.Equal(["sysmain"], ids);
            return new(true, 1, null);
        };

        var result = await ComparisonPlanner.RunAsync(Request("sysmain", "timer-res"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ComparisonStage.Apply, result.FailedStage);
        Assert.True(result.RolledBack);
        Assert.Equal(["sysmain"], result.AppliedIds);
    }

    [Fact]
    public async Task After_capture_failure_rolls_back_applied_items()
    {
        var beforeCsv = WriteFrameCsv("b3.csv");
        var callCount = 0;
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(
            ++callCount == 1 ? SuccessfulRun(beforeCsv) : FailedRun("no-process"));
        var backup = Path.Combine(_root, "backup2.json");
        File.WriteAllText(backup, "{}");
        ComparisonPlanner.ApplyOverride = (_, _, _) =>
            Task.FromResult(new ComparisonApplyOutcome(true, null, backup, ["sysmain", "timer-res"]));
        List<string>? restored = null;
        ComparisonPlanner.RestoreOverride = (_, ids) => { restored = ids.ToList(); return new ComparisonRollbackResult(true, ids.Count, null); };
        ComparisonPlanner.CooldownOverride = (_, _) => Task.CompletedTask;

        var result = await ComparisonPlanner.RunAsync(Request("sysmain", "timer-res"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("no-process", result.ReasonCode);
        Assert.Equal(ComparisonStage.After, result.FailedStage);
        Assert.Contains("还原", result.Error);
        Assert.True(result.RolledBack);
        Assert.Null(result.RollbackError);
        Assert.NotNull(restored);
        Assert.Equal(["sysmain", "timer-res"], restored);
    }

    [Fact]
    public async Task Failed_rollback_is_reported_not_hidden()
    {
        var beforeCsv = WriteFrameCsv("b4.csv");
        var callCount = 0;
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(
            ++callCount == 1 ? SuccessfulRun(beforeCsv) : FailedRun("no-process"));
        var backup = Path.Combine(_root, "backup3.json");
        File.WriteAllText(backup, "{}");
        ComparisonPlanner.ApplyOverride = (_, _, _) =>
            Task.FromResult(new ComparisonApplyOutcome(true, null, backup, ["sysmain"]));
        ComparisonPlanner.RestoreOverride = (_, _) => new ComparisonRollbackResult(false, 0, "备份文件被占用");

        var result = await ComparisonPlanner.RunAsync(Request("sysmain"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.RolledBack);
        Assert.NotNull(result.RollbackError);
        Assert.Contains("占用", result.RollbackError);
        Assert.Contains("自动还原失败", result.Error);
    }

    [Fact]
    public async Task Verdict_rollback_decision_keeps_backupfile_for_manual_rollback()
    {
        var beforeCsv = WriteFrameCsv("b5.csv", ms: 1000.0 / 100);
        var afterCsv = WriteFrameCsv("a5.csv", ms: 1000.0 / 100.2); // 效应过小 → negligible → Rollback
        var backup = Path.Combine(_root, "backup4.json");
        File.WriteAllText(backup, "{}");
        var runs = 0;
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(SuccessfulRun(++runs == 1 ? beforeCsv : afterCsv));
        ComparisonPlanner.ApplyOverride = (_, _, _) =>
            Task.FromResult(new ComparisonApplyOutcome(true, null, backup, ["sysmain"]));
        ComparisonPlanner.CooldownOverride = (_, _) => Task.CompletedTask;
        var manualRollbacks = 0;
        ComparisonPlanner.RestoreOverride = (_, ids) => { manualRollbacks++; return new ComparisonRollbackResult(true, ids.Count, null); };

        var result = await ComparisonPlanner.RunAsync(Request("sysmain"), CancellationToken.None);

        // 判定为建议回滚时，流程不自动还原——由用户在结论卡上按「立即回滚」。
        Assert.True(result.Ok);
        Assert.Equal(GainDecision.Rollback, result.Verdict!.Decision);
        Assert.False(result.RolledBack);

        var rollback = ComparisonPlanner.Rollback(result.BackupFile, result.AppliedIds);
        Assert.True(rollback.Ok);
        Assert.Equal(1, rollback.RestoredCount);
        Assert.Equal(1, manualRollbacks);
    }

    [Fact]
    public async Task Empty_items_are_rejected()
    {
        var result = await ComparisonPlanner.RunAsync(Request(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("no-items", result.ReasonCode);
    }

    [Fact]
    public async Task Unparseable_after_data_reports_verdict_failure()
    {
        var beforeCsv = WriteFrameCsv("b6.csv");
        var badPath = Path.Combine(_root, "bad.csv");
        await File.WriteAllLinesAsync(badPath, ["foo,bar", "1,2"]);
        var backup = Path.Combine(_root, "backup5.json");
        File.WriteAllText(backup, "{}");
        var runs = 0;
        ComparisonPlanner.CaptureOverride = (_, _) => Task.FromResult(
            SuccessfulRun(++runs == 1 ? beforeCsv : badPath));
        ComparisonPlanner.ApplyOverride = (_, _, _) =>
            Task.FromResult(new ComparisonApplyOutcome(true, null, backup, ["sysmain"]));
        ComparisonPlanner.CooldownOverride = (_, _) => Task.CompletedTask;

        var result = await ComparisonPlanner.RunAsync(Request("sysmain"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("verdict-failed", result.ReasonCode);
        Assert.Contains("无法解析", result.Error);
    }
}
