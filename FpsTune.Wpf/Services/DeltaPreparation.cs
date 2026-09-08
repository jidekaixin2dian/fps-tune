using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>开局准备只提供可审阅的选择，不执行系统修改。</summary>
public static class DeltaPreparation
{
    public const string GameExecutable = "DeltaForceClient-Win64-Shipping.exe";
    private static readonly string[] BasicIds = { "game-mode", "dvr-off", "gpu-pref" };
    public static bool IsGameExecutable(string? path) => !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetFileName(path), GameExecutable, StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<string> PendingItems(IEnumerable<OptimizationItem> items, bool gameReady)
        => gameReady ? items.Where(i => !i.Optimized && BasicIds.Contains(i.Id)).Select(i => i.Id).ToArray() : [];
}
