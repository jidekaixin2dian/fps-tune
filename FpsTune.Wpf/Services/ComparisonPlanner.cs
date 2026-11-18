using System.IO;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>对照流程的阶段（结论卡按此显示进度）。</summary>
public enum ComparisonStage
{
    /// <summary>改前采样。</summary>
    Before,
    /// <summary>应用所选项目（写入仍走既有审阅确认，不由本流程自动写系统）。</summary>
    Apply,
    /// <summary>冷却等待（写入生效、帧率回落稳态）。</summary>
    Cooldown,
    /// <summary>改后采样。</summary>
    After,
    /// <summary>判定。</summary>
    Verdict,
}

/// <summary>对照请求：所选优化项 + 目标游戏（路径锚）。</summary>
public sealed record ComparisonRequest(
    IReadOnlyList<string> Items,
    string? GamePath,
    int SampleSeconds = 60,
    int CooldownSeconds = 10);

/// <summary>应用步骤的回执：成败、错误、备份快照与真正改动项（对照的回滚依据）。</summary>
public sealed record ComparisonApplyOutcome(
    bool Ok, string? Error, string? BackupFile, IReadOnlyList<string> ChangedIds);

/// <summary>一键回滚的结果（沿用 BackupService.RestoreAll 的口径）。</summary>
public sealed record ComparisonRollbackResult(bool Ok, int RestoredCount, string? Error);

/// <summary>
/// 对照全流程结果。Ok=false 时 Error 说明在哪一步中止（含自动还原结果）、ReasonCode 是机器分类；
/// Verdict 在流程完成时给出。BackupFile 留给结论卡的一键回滚。
/// </summary>
public sealed record ComparisonResult(
    bool Ok,
    string? Error,
    string? ReasonCode,
    GainVerdictResult? Verdict,
    CaptureRun? Before,
    CaptureRun? After,
    string? BackupFile,
    IReadOnlyList<string> AppliedIds,
    bool RolledBack,
    string? RollbackError,
    ComparisonStage? FailedStage = null);

/// <summary>
/// 自动前后对照的状态机（0.5.0 M2）：改前采样 → 应用 → 冷却 → 改后采样 → 判定。
///
/// 安全规则（与 A/B 实验管线同源）：
/// - 应用必须先取得本流程自己的备份快照（<see cref="NativeOptimizationEngine.ApplyItemsWithReceiptAsync"/> 内含），
///   没有快照就中止且不采样；
/// - 应用失败或改后采样失败，按快照自动还原已改动项并如实报告还原结果；
/// - 写入由调用方提供的应用回调执行——GUI 侧弹既有审阅窗确认后才写系统，本流程绝不绕过审阅；
/// - 判定只读两侧 frames.csv，不再写任何系统状态。
/// </summary>
public static class ComparisonPlanner
{
    // 测试注入点：采样 / 应用 / 还原 / 冷却（沿用项目既有注入习惯）。
    internal static Func<CaptureRequest, CancellationToken, Task<CaptureRun>>? CaptureOverride;
    internal static Func<IReadOnlyList<string>, string?, CancellationToken, Task<ComparisonApplyOutcome>>? ApplyOverride;
    internal static Func<string?, IReadOnlyList<string>, ComparisonRollbackResult>? RestoreOverride;
    internal static Func<int, CancellationToken, Task>? CooldownOverride;

    public static async Task<ComparisonResult> RunAsync(
        ComparisonRequest request, CancellationToken ct, IProgress<ComparisonStage>? progress = null,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ComparisonApplyOutcome>>? applyOverride = null)
    {
        var items = request.Items.Distinct(StringComparer.Ordinal).ToArray();
        if (items.Length == 0)
            return new ComparisonResult(false, "没有选择要对照的优化项。", "no-items",
                null, null, null, null, [], false, null);

        using var trace = OperationDiagnostics.Begin("comparison",
            $"{items.Length} 项 · {request.SampleSeconds}s × 2", null);
        var stage = ComparisonStage.Before;
        try
        {
            // ---- 改前采样 ----
            progress?.Report(ComparisonStage.Before);
            trace.Stage("before");
            var before = await CaptureAsync(request, ct).ConfigureAwait(false);
            if (before.Error is not null)
            {
                trace.Item("before", "failed", before.ReasonCode);
                trace.Settle("failed");
                return new ComparisonResult(false,
                    ClassifyCaptureReason(before.ReasonCode, before.Error), before.ReasonCode,
                    null, before, null, null, [], false, null, stage);
            }
            trace.Item("before", "succeeded", $"{before.Frames} 帧");

            // ---- 应用 ----
            stage = ComparisonStage.Apply;
            progress?.Report(stage);
            trace.Stage("apply");
            var applied = applyOverride is { } applyLocal
                ? await applyLocal(items, request.GamePath, ct).ConfigureAwait(false)
                : ApplyOverride is { } applyInject
                    ? await applyInject(items, request.GamePath, ct).ConfigureAwait(false)
                    : await ApplyDefaultAsync(items, request.GamePath, ct).ConfigureAwait(false);
            if (!applied.Ok)
            {
                trace.Item("apply", "failed", applied.Error);
                trace.Settle("failed");
                var rollback = applied.ChangedIds.Count > 0
                    ? RollbackInternal(applied.BackupFile, applied.ChangedIds, trace)
                    : null;
                return new ComparisonResult(false, "应用所选项目没有成功：" + applied.Error + RollbackNote(rollback),
                    "apply-failed", null, before, null, applied.BackupFile, applied.ChangedIds,
                    rollback?.Ok == true, rollback is { Ok: false } ? rollback.Error : null, stage);
            }
            if (string.IsNullOrEmpty(applied.BackupFile) || !File.Exists(applied.BackupFile))
            {
                trace.Item("apply", "failed", "no-backup");
                trace.Settle("failed");
                return new ComparisonResult(false,
                    "应用已完成，但没有取得本次对照自己的备份快照，已停止对照以保证可还原。", "no-backup",
                    null, before, null, applied.BackupFile, [], false, null, stage);
            }
            trace.Item("apply", "succeeded", $"{applied.ChangedIds.Count} 项改动");

            // ---- 冷却 ----
            stage = ComparisonStage.Cooldown;
            progress?.Report(stage);
            trace.Stage("cooldown");
            if (CooldownOverride is { } cooldownInject)
                await cooldownInject(request.CooldownSeconds, ct).ConfigureAwait(false);
            else
                await Task.Delay(TimeSpan.FromSeconds(request.CooldownSeconds), ct).ConfigureAwait(false);

            // ---- 改后采样 ----
            stage = ComparisonStage.After;
            progress?.Report(stage);
            trace.Stage("after");
            var after = await CaptureAsync(request, ct).ConfigureAwait(false);
            if (after.Error is not null)
            {
                trace.Item("after", "failed", after.ReasonCode);
                // 改后采样失败时对照无法完成：安全优先，按快照还原已改动项。
                var rollback = RollbackInternal(applied.BackupFile, applied.ChangedIds, trace);
                var note = RollbackNote(rollback);
                trace.Settle("failed");
                return new ComparisonResult(false,
                    ClassifyCaptureReason(after.ReasonCode, after.Error) + note, after.ReasonCode,
                    null, before, null, applied.BackupFile, applied.ChangedIds,
                    rollback?.Ok == true, rollback is { Ok: false } ? rollback.Error : null, stage);
            }
            trace.Item("after", "succeeded", $"{after.Frames} 帧");

            // ---- 判定 ----
            stage = ComparisonStage.Verdict;
            progress?.Report(stage);
            trace.Stage("verdict");
            GainVerdictResult? verdict = null;
            string? verdictError = null;
            try
            {
                var beforeFrames = await Task.Run(
                    () => FrameTimeStats.ReadSeries(File.ReadLines(before.CsvPath), ct, FrameTimeStats.MaxFrameCount),
                    ct).ConfigureAwait(false);
                var afterFrames = await Task.Run(
                    () => FrameTimeStats.ReadSeries(File.ReadLines(after.CsvPath), ct, FrameTimeStats.MaxFrameCount),
                    ct).ConfigureAwait(false);
                FrameParseError? be = beforeFrames.Error;
                FrameParseError? ae = afterFrames.Error;
                if (be is not null || ae is not null)
                {
                    verdictError = be is not null
                        ? "改前数据无法解析，判定中止。"
                        : "改后数据无法解析，判定中止。";
                    trace.Item("verdict", "failed", be is not null ? "before-parse" : "after-parse");
                }
                else
                {
                    verdict = GainVerdict.Evaluate(
                        GainVerdict.BuildRounds(beforeFrames.FrameTimesMs),
                        GainVerdict.BuildRounds(afterFrames.FrameTimesMs));
                    trace.Item("verdict", "succeeded", verdict.Decision.ToString());
                    AppendArchive(request, verdict, before, after, applied.BackupFile);
                }
            }
            catch (Exception ex)
            {
                verdictError = "判定失败：" + ex.Message;
                trace.Item("verdict", "failed", "error");
            }

            trace.Settle(verdict is null ? "partial" : "succeeded");
            return new ComparisonResult(
                verdict is not null, verdictError,
                verdict is null ? "verdict-failed" : null,
                verdict, before, after, applied.BackupFile, applied.ChangedIds,
                RolledBack: false, RollbackError: null, FailedStage: verdict is null ? stage : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace.Fail(ex);
            return new ComparisonResult(false, "对照流程异常中止：" + ex.Message, "unexpected",
                null, null, null, null, [], false, null, stage);
        }
    }

    /// <summary>结论卡上的一键回滚：按本流程自己的备份快照还原已改动项。</summary>
    public static ComparisonRollbackResult Rollback(string? backupFile, IReadOnlyList<string> changedIds)
    {
        using var trace = OperationDiagnostics.Begin("comparison-rollback", $"{changedIds.Count} 项", null);
        var result = RollbackInternal(backupFile, changedIds, trace);
        trace.Settle(result?.Ok == true ? "succeeded" : "failed");
        if (result is null)
            return new ComparisonRollbackResult(false, 0, "没有需要还原的改动。");
        return result;
    }

    /// <summary>测试注入：档案条目构造（默认由对照结果生成，指纹取当前机器）。</summary>
    internal static Func<ComparisonResult, GainArchiveEntry>? ArchiveEntryOverride;

    /// <summary>对照结论入册收益档案（M3）。档案是辅助记录，写入失败不阻塞对照。</summary>
    private static void AppendArchive(
        ComparisonRequest request, GainVerdictResult verdict, CaptureRun before, CaptureRun after, string backupFile)
    {
        try
        {
            var entry = ArchiveEntryOverride?.Invoke(new ComparisonResult(
                true, null, null, verdict, before, after, backupFile, request.Items, false, null))
                ?? new GainArchiveEntry(
                    GameId: string.IsNullOrWhiteSpace(request.GamePath)
                        ? request.GamePath ?? "unknown"
                        : Path.GetFileNameWithoutExtension(request.GamePath),
                    At: DateTimeOffset.Now,
                    Items: request.Items.ToArray(),
                    Verdict: verdict.Decision switch
                    {
                        GainDecision.Keep => "keep",
                        GainDecision.Rollback => "rollback",
                        _ => "insufficient",
                    },
                    AvgBase: verdict.Analysis.AvgBase,
                    AvgTest: verdict.Analysis.AvgTest,
                    AvgDiffPct: verdict.Analysis.AvgDiffPct,
                    AvgDiffLowerPct: verdict.Analysis.AvgDiffLowerPct,
                    AvgDiffUpperPct: verdict.Analysis.AvgDiffUpperPct,
                    P1DiffPct: verdict.Analysis.P1DiffPct,
                    P99DiffPct: verdict.Analysis.P99DiffPct,
                    SegmentsBefore: verdict.SegmentsBefore,
                    SegmentsAfter: verdict.SegmentsAfter,
                    SampleSeconds: request.SampleSeconds,
                    BackupFile: backupFile,
                    RolledBack: false,
                    MachineFingerprint: GainArchiveService.CurrentFingerprint());
            GainArchiveService.Append(entry);
        }
        catch
        {
            // 档案写入失败不阻塞对照；错误由 GainArchiveService 内部记录。
        }
    }

    private static ComparisonRollbackResult? RollbackInternal(
        string? backupFile, IReadOnlyList<string> changedIds, OperationDiagnostics.Trace trace)
    {
        if (changedIds.Count == 0)
        {
            trace.Item("rollback", "skipped", "no-changes");
            return new ComparisonRollbackResult(true, 0, null);
        }
        var outcome = string.IsNullOrWhiteSpace(backupFile)
            ? new ComparisonRollbackResult(false, 0, Str.T("Str.NoRestorableBackups"))
            : RestoreOverride is { } restoreInject
                ? restoreInject(backupFile, changedIds)
                : RestoreDefault(backupFile, changedIds);
        trace.Item("rollback", outcome.Ok ? "succeeded" : "failed",
            outcome.Ok ? $"{outcome.RestoredCount} 项" : outcome.Error);
        return outcome;
    }

    private static ComparisonRollbackResult RestoreDefault(string backupFile, IReadOnlyList<string> changedIds)
    {
        return Task.Run(() =>
        {
            var result = BackupService.RestoreOperationBackup(backupFile, changedIds);
            if (result.Failures.Count > 0)
                return new ComparisonRollbackResult(false, result.Restored.Count, string.Join("；", result.Failures));
            return new ComparisonRollbackResult(true, result.Restored.Count, null);
        }).GetAwaiter().GetResult();
    }

    private static async Task<CaptureRun> CaptureAsync(ComparisonRequest request, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(request.GamePath)
            ? null
            : Path.GetFileNameWithoutExtension(request.GamePath);
        var captureRequest = new CaptureRequest(
            name ?? "", GamePath: request.GamePath, DurationSec: request.SampleSeconds, Label: "comparison");
        return CaptureOverride is { } injected
            ? await injected(captureRequest, ct).ConfigureAwait(false)
            : await CaptureOrchestrator.CaptureAsync(captureRequest, ct).ConfigureAwait(false);
    }

    private static async Task<ComparisonApplyOutcome> ApplyDefaultAsync(
        IReadOnlyList<string> ids, string? gamePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var receipt = await NativeOptimizationEngine.ApplyItemsWithReceiptAsync(ids, gamePath).ConfigureAwait(false);
        var changed = receipt.Results.Where(r => r.Changed || r.StateUncertain).Select(r => r.Id).ToList();
        var errors = receipt.Results.Where(r => !r.Ok && !r.Skipped).Select(r => r.Id + " — " + r.Message).ToList();
        return new ComparisonApplyOutcome(errors.Count == 0,
            errors.Count == 0 ? null : string.Join("; ", errors), receipt.BackupFile, changed);
    }

    /// <summary>采样失败原因的统一措辞（见 <see cref="CaptureGuidance"/>）：对照这一侧不再自备一套文案。</summary>
    private static string ClassifyCaptureReason(string? reasonCode, string fallback)
        => CaptureGuidance.Describe(reasonCode, fallback);

    private static string RollbackNote(ComparisonRollbackResult? rollback) => rollback switch
    {
        null => "",
        { Ok: true, RestoredCount: > 0 } => "；已按备份还原本次改动。",
        { Ok: true } => "；本次没有产生可还原的改动。",
        { Ok: false } => "；且自动还原失败：" + rollback.Error,
    };
}
