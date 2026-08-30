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
    private static readonly object OperationLock = new();

    public static Task<RunResult> DetectAsync()
    {
        var gamePath = AppState.GamePath;
        return Task.Run(() => new RunResult(0, DetectionService.BuildDetectJson(gamePath), ""));
    }

    /// <summary>预设 -> 优化项 id 列表。未知预设回退到 balanced。</summary>
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
            lock (OperationLock)
            {
                var result = BackupService.RestoreAll();
                if (result.Restored.Count == 0 && result.Failures.Count == 0)
                    return new RunResult(0, "没有找到可还原的备份。", "");

                var sb = new StringBuilder();
                sb.AppendLine("== 还原结果 ==");
                foreach (var (file, id) in result.Restored)
                    sb.AppendLine($"[已还原] {Path.GetFileName(file)}  {id}");
                foreach (var failure in result.Failures)
                    sb.AppendLine("[失败] " + failure);
                sb.AppendLine();
                sb.Append($"汇总：{result.Restored.Count} 项已还原、{result.Failures.Count} 项失败。");
                if (result.Restored.Count > 0 && result.Failures.Count == 0)
                    sb.Append("已处理的备份文件已重命名为 .restored（保留供审计）。");
                return new RunResult(result.Failures.Count == 0 ? 0 : 1, sb.ToString(), "");
            }
        });

    public static Task<RunResult> ListRestoreAsync()
    {
        IReadOnlyList<string> files;
        lock (OperationLock)
            files = BackupService.ListBackups();
        var payload = new { backups = files, count = files.Count };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(new RunResult(0, json, ""));
    }

    private static RunResult ApplyNative(IReadOnlyList<string> ids, string? gamePath)
    {
        lock (OperationLock)
        {
            // 备份与应用必须是一个串行临界区，避免两次 Apply 互相覆盖快照。
            var backupFile = BackupService.Capture(ids, gamePath);
            var results = NativeOptimizationEngine.ApplyAll(ids, gamePath);

            var rebootIds = results
                .Where(r => r.Ok && r.Changed)
                .Select(r => ItemCatalog.All.FirstOrDefault(x => x.Id == r.Id))
                .Where(def => def?.Reboot == true)
                .Select(def => def!.Id)
                .ToArray();

            var payload = new
            {
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
                    message = r.Message
                }),
                summary = $"{results.Count(r => r.Ok)} 成功、{results.Count(r => !r.Ok && !r.Skipped)} 失败、{results.Count(r => r.Skipped)} 跳过",
                backupFile = backupFile,
                reboot = rebootIds
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            return new RunResult(0, json, "");
        }
    }
}
