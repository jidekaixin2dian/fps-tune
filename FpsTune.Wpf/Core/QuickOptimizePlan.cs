using System.Linq;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 一键优化统一页的方案解析：catalog 预设 + 可选电源计划项合并为一份执行清单。
/// 执行与备份契约仍由 <see cref="OptimizationEngine"/> 负责，这里只做纯映射（可单测）。
/// </summary>
public static class QuickOptimizePlan
{
    internal const string PowerOverlayPerfId = "power-overlay-perf";

    /// <summary>解析执行清单；未知预设按 catalog 约定抛错。</summary>
    public static IReadOnlyList<string> Resolve(string preset, bool applyPowerPlan)
    {
        var ids = OptimizationCatalog.ResolvePreset(preset).ToList();
        if (applyPowerPlan && !ids.Contains(PowerOverlayPerfId, StringComparer.Ordinal))
            ids.Add(PowerOverlayPerfId);
        return ids;
    }
}
