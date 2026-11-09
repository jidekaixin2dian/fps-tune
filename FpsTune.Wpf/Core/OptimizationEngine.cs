namespace FpsTune.Wpf.Core;

using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Services;

/// <summary>
/// C# 核心引擎入口。
/// Apply / Detect / Restore 均已走 C# 原生实现，不再依赖 PowerShell 作为核心路径。
/// PowerShell 脚本仍随包保留，供兼容/兜底排查使用。
/// </summary>
public static class OptimizationEngine
{
    public static Task<RunResult> DetectAsync()
    {
        var gamePath = AppState.GamePath;
        return Task.Run(() => new RunResult(0, DetectionService.BuildDetectJson(gamePath), ""));
    }

    /// <summary>预设 -> 优化项 id 列表。未知预设明确报错。</summary>
    internal static IReadOnlyList<string> GetPresetIds(string preset)
        => OptimizationCatalog.ResolvePreset(preset);

    public static Task<RunResult> ApplyPresetAsync(string preset)
        => ApplyPresetAsync(preset, AppState.GamePath);

    /// <summary>
    /// 使用调用方明确提供的游戏路径应用预设；不会改写 AppState，避免并发操作串错目标。
    /// </summary>
    public static Task<RunResult> ApplyPresetAsync(string preset, string? gamePath)
    {
        var itemIds = GetPresetIds(preset).ToArray();
        return Task.Run(() => ApplyNative(itemIds, gamePath));
    }

    public static Task<RunResult> ApplyItemsAsync(IEnumerable<string> ids)
        => ApplyItemsAsync(ids, AppState.GamePath);

    /// <summary>
    /// 使用调用方明确提供的游戏路径应用项目；不会改写 AppState，避免自动应用时备份/修改目标漂移。
    /// </summary>
    public static Task<RunResult> ApplyItemsAsync(IEnumerable<string> ids, string? gamePath)
    {
        var itemIds = ids.Distinct().ToArray();
        return Task.Run(() => ApplyNative(itemIds, gamePath));
    }

    public static Task<RunResult> RestoreAsync()
        => Task.Run(() =>
        {
            var result = RecoveryCoordinator.RestoreAll();
            if (result.Restored.Count == 0 && result.Failures.Count == 0)
                return new RunResult(0, "没有找到可还原的备份。", "");

            var sb = new StringBuilder();
            sb.AppendLine("== 还原结果 ==");
            foreach (var (file, id) in result.Restored)
                sb.AppendLine($"[已还原] {Path.GetFileName(file)}  {id}");
            foreach (var stale in result.ArchivedStale)
                sb.AppendLine($"[已归档] {stale} → .stale");
            foreach (var failure in result.Failures)
                sb.AppendLine("[失败] " + failure);
            sb.AppendLine();
            sb.Append($"汇总：{result.Restored.Count} 项已还原、{result.Failures.Count} 项失败、{result.ArchivedStale.Count} 份失效备份已归档。");
            if (result.Restored.Count > 0 && result.Failures.Count == 0)
                sb.Append("恢复已完成；系统快照保留为 .restored，其他模块在验证后消费备份。");
            return new RunResult(result.Failures.Count == 0 ? 0 : 1, sb.ToString(), "");
        });

    public static Task<RunResult> ListRestoreAsync()
    {
        var files = BackupService.ListBackups();
        var scopes = RecoveryCoordinator.Inventory();
        var payload = new { backups = files, count = files.Count, scopes };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(new RunResult(0, json, ""));
    }

    internal sealed record ApplyReceipt(string OperationId, string BackupFile, IReadOnlyList<OptimizationApplyResult> Results);

    internal static Task<ApplyReceipt> ApplyItemsWithReceiptAsync(IEnumerable<string> ids, string? gamePath, Action<string>? beforeWrite = null)
        => Task.Run(() => ExecuteNative(ids.Distinct().ToArray(), gamePath, beforeWrite));

    /// <summary>
    /// 同步版 receipt 应用：仅供开局准备等已在同一同步工作线程持有 SystemMutationGate 的调用方使用。
    /// 命名互斥在同一线程上重入。
    /// </summary>
    internal static ApplyReceipt ApplyItemsWithReceipt(IEnumerable<string> ids, string? gamePath)
        => ExecuteNative(ids.Distinct().ToArray(), gamePath);

    private static ApplyReceipt ExecuteNative(IReadOnlyList<string> ids, string? gamePath, Action<string>? beforeWrite = null)
    {
        // 应用优化是用户最常反馈"执行失败"的入口，过去结果只在界面上一闪而过、诊断报告里什么也没有。
        // 这里统一落一条逐项回执：成功/失败/跳过各是什么、卡在哪个阶段、是否为管理员。
        using var trace = OperationDiagnostics.Begin("optimize-apply", DescribeTarget(ids, gamePath), null);
        try
        {
            trace.Stage("precheck");
            using var mutation = SystemMutationGate.Acquire();
            if (ids.Count == 0 || ids.Any(id => !ItemCatalog.All.Any(item => item.Id == id)))
                throw new ArgumentException("Unknown or empty optimization selection.", nameof(ids));
            var adminItems = ids
                .Select(id => ItemCatalog.All.First(item => item.Id == id))
                .Where(def => def is { Admin: true, Available: true })
                .ToArray();
            if (!AdminHelper.IsAdministrator() && adminItems.Length > 0)
                throw new UnauthorizedAccessException(
                    Str.T("Str.OptApplyNeedsAdminItems", adminItems.Length,
                        string.Join(Str.T("Str.ListSeparator"), adminItems.Select(def => def.DisplayName))));
            BackupService.EnsureNoPendingWrites(ids);
            trace.Stage("capture");
            var backupFile = BackupService.Capture(Array.Empty<string>(), gamePath);
            // Persist the caller's operation anchor before the first system write.
            beforeWrite?.Invoke(backupFile);
            trace.Stage("apply");
            var results = NativeOptimizationEngine.ApplyAll(ids, gamePath,
                id => BackupService.AppendCapture(backupFile, id, gamePath),
                result => BackupService.CompleteCapture(backupFile, result));
            foreach (var result in results)
            {
                // 回执里写"人话 + 原始异常"：支撑时看得到类型与原文，用户读到的仍是能照做的下一步。
                var outcome = result.Skipped ? "skipped" : result.Ok ? "succeeded" : "failed";
                var detail = outcome switch
                {
                    "succeeded" => null,
                    "skipped" => result.Message,
                    _ => result.Detail is { Length: > 0 } raw && !raw.EndsWith(result.Message, StringComparison.Ordinal)
                        ? Str.T("Str.OptItemFailureDetail", result.Message, raw)
                        : result.Message
                };
                trace.Item(result.Id, outcome, detail);
            }
            trace.Stage("finalize");
            var failed = results.Count(r => !r.Ok && !r.Skipped);
            var applied = results.Count(r => r.Ok && !r.Skipped);
            trace.Settle(failed == 0 ? "succeeded" : applied == 0 ? "failed" : "partial");
            return new ApplyReceipt(BackupService.OperationId(backupFile), backupFile, results);
        }
        catch (Exception ex)
        {
            // 被拦下（缺管理员、上一笔未确认、忙碌）同样是"执行失败"的一种，必须留痕才查得动。
            trace.Fail(ex);
            throw;
        }
    }

    /// <summary>回执里的目标描述：只留游戏文件名，不写完整路径（诊断报告会脱敏，但没必要先写进去）。</summary>
    private static string DescribeTarget(IReadOnlyList<string> ids, string? gamePath)
    {
        var game = string.IsNullOrWhiteSpace(gamePath) ? "" : Path.GetFileName(gamePath);
        return string.IsNullOrWhiteSpace(game)
            ? Str.T("Str.OptApplyTargetNoGame", ids.Count)
            : Str.T("Str.OptApplyTargetGame", ids.Count, game);
    }

    private static RunResult ApplyNative(IReadOnlyList<string> ids, string? gamePath)
    {
        var receipt = ExecuteNative(ids, gamePath);
        var backupFile = receipt.BackupFile;
        var results = receipt.Results;
            var rebootIds = results
                .Where(r => r.Ok && r.Changed)
                .Select(r => ItemCatalog.All.FirstOrDefault(x => x.Id == r.Id))
                .Where(def => def?.Reboot == true)
                .Select(def => def!.Id)
                .ToArray();

            var payload = new
            {
                operationId = receipt.OperationId,
                tool = "fps-tune",
                version = UpdateService.CurrentVersion,
                mode = "apply",
                results = results.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    ok = r.Ok,
                    changed = r.Changed,
                    skipped = r.Skipped,
                    message = r.Message,
                    stateUncertain = r.StateUncertain
                }),
                summary = $"{results.Count(r => r.Ok)} 成功、{results.Count(r => !r.Ok && !r.Skipped)} 失败、{results.Count(r => r.Skipped)} 跳过",
                backupFile = backupFile,
                reboot = rebootIds
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            return new RunResult(results.Any(r => !r.Ok && !r.Skipped) ? 1 : 0, json, "");
    }
}
