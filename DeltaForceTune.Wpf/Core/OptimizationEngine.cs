namespace DeltaForceTune.Wpf.Core;

using System.Linq;
using System.Text.Json;
using DeltaForceTune.Wpf.Services;

/// <summary>
/// C# 核心引擎入口。
/// Apply / Detect / Restore 均已走 C# 原生实现，不再依赖 PowerShell 作为核心路径。
/// PowerShell 脚本仍随包保留，供兼容/兜底排查使用。
/// </summary>
public static class OptimizationEngine
{
    private static readonly string[] SafeOnlyIds =
        { "game-mode", "dvr-off", "transparency-off", "fso-off", "gpu-pref" };

    private static readonly string[] BalancedExclude =
        { "sysmain-off", "wsearch-off", "hibernate-off", "power-tuning" };

    public static Task<RunResult> DetectAsync()
        => Task.Run(() => new RunResult(0, DetectionService.BuildDetectJson(AppState.GamePath), ""));

    public static Task<RunResult> ApplyPresetAsync(string preset)
    {
        IEnumerable<string> ids = preset switch
        {
            "full" => ItemCatalog.All.Select(x => x.Id),
            "safe-only" => SafeOnlyIds,
            _ => ItemCatalog.All.Where(x => !BalancedExclude.Contains(x.Id)).Select(x => x.Id)
        };
        return Task.Run(() => ApplyNative(ids));
    }

    public static Task<RunResult> ApplyItemsAsync(IEnumerable<string> ids)
        => Task.Run(() => ApplyNative(ids));

    public static Task<RunResult> RestoreAsync()
        => Task.Run(() =>
        {
            var restored = BackupService.RestoreLatest();
            if (restored is null)
                return new RunResult(0, "没有找到可还原的备份。", "");

            return new RunResult(0, $"已从备份还原：{restored}", "");
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
            tool = "delta-force-tune",
            version = "1.0.0",
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
