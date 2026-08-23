namespace DeltaForceTune.Wpf.Core;

using System.Linq;
using System.Text;
using System.Text.Json;
using DeltaForceTune.Wpf.Services;

/// <summary>
/// C# 核心引擎入口。
/// Apply 已经走 C# 原生实现；Detect / Restore 仍保留 PowerShell 兼容层，
/// 后续会继续迁移。
/// </summary>
public static class OptimizationEngine
{
    private static readonly string[] SafeOnlyIds =
        { "game-mode", "dvr-off", "transparency-off", "fso-off", "gpu-pref" };

    private static readonly string[] BalancedExclude =
        { "sysmain-off", "wsearch-off", "hibernate-off", "power-tuning" };

    public static Task<RunResult> DetectAsync()
        => PowerShellRunner.RunAsync(ScriptLocator.Resolve("delta-optimizer.ps1"), "-Detect", "-Json");

    public static Task<RunResult> ApplyPresetAsync(string preset)
    {
        IEnumerable<string> ids = preset switch
        {
            "full" => ItemCatalog.All.Select(x => x.Id),
            "safe-only" => SafeOnlyIds,
            _ => ItemCatalog.All.Where(x => !BalancedExclude.Contains(x.Id)).Select(x => x.Id)
        };
        return Task.FromResult(ApplyNative(ids));
    }

    public static Task<RunResult> ApplyItemsAsync(IEnumerable<string> ids)
        => Task.FromResult(ApplyNative(ids));

    public static Task<RunResult> RestoreAsync()
    {
        var restored = BackupService.RestoreLatest();
        if (restored is not null)
            return Task.FromResult(new RunResult(0, $"已从备份还原：{restored}", ""));

        return PowerShellRunner.RunAsync(ScriptLocator.Resolve("delta-optimizer.ps1"), "-Restore", "-Json");
    }

    public static Task<RunResult> ListRestoreAsync()
    {
        var files = BackupService.ListBackups();
        var payload = new { backups = files, count = files.Count };
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(new RunResult(0, json, ""));
    }

    private static RunResult ApplyNative(IEnumerable<string> ids)
    {
        var itemIds = ids.Distinct().ToList();
        var backupFile = BackupService.Capture(itemIds, AppState.GamePath);
        var results = NativeOptimizationEngine.ApplyAll(itemIds, AppState.GamePath);

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
            backupFile = backupFile
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new RunResult(0, json, "");
    }
}
