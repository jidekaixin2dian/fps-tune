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
        => Task.Run(() => new RunResult(0, DetectionService.BuildDetectJson(AppState.GamePath), ""));

    /// <summary>预设 -> 优化项 id 列表。未知预设回退到 balanced。</summary>
    internal static IReadOnlyList<string> GetPresetIds(string preset)
        => OptimizationCatalog.ResolvePreset(preset);

    public static Task<RunResult> ApplyPresetAsync(string preset)
        => Task.Run(() => ApplyNative(GetPresetIds(preset)));

    public static Task<RunResult> ApplyItemsAsync(IEnumerable<string> ids)
        => Task.Run(() => ApplyNative(ids));

    public static Task<RunResult> RestoreAsync()
        => Task.Run(() =>
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
            if (result.Restored.Count > 0)
                sb.Append("已处理的备份文件已重命名为 .restored（保留供审计）。");
            return new RunResult(0, sb.ToString(), "");
        });

    public static Task<RunResult> ListRestoreAsync()
    {
        var files = BackupService.ListBackups();
        var payload = new { backups = files, count = files.Count };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(new RunResult(0, json, ""));
    }

    private static RunResult ApplyNative(IEnumerable<string> ids)
    {
        var itemIds = ids.Distinct().ToList();
        var backupFile = BackupService.Capture(itemIds, AppState.GamePath);
        var results = NativeOptimizationEngine.ApplyAll(itemIds, AppState.GamePath);

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
