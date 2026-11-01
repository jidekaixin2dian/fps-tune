using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 一键优化统一页专项：方案档位到执行清单的纯映射（catalog 预设 + 可选电源计划项）。
/// 不触碰真实系统，只验证解析与合并契约。
/// </summary>
public sealed class QuickOptimizeTests : IDisposable
{
    private readonly string _dir;

    public QuickOptimizeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        UserDataPaths.RootOverride = _dir;
    }

    public void Dispose() => UserDataPaths.RootOverride = null;

    [Fact]
    public void Balanced_plan_resolves_catalog_ids()
    {
        var ids = QuickOptimizePlan.Resolve("balanced", applyPowerPlan: false);
        Assert.Equal(OptimizationCatalog.ResolvePreset("balanced"), ids);
    }

    [Fact]
    public void Safe_plan_resolves_catalog_ids()
    {
        var ids = QuickOptimizePlan.Resolve("safe-only", applyPowerPlan: false);
        Assert.Equal(OptimizationCatalog.ResolvePreset("safe-only"), ids);
    }

    [Fact]
    public void Full_plan_resolves_every_available_item()
    {
        var ids = QuickOptimizePlan.Resolve("full", applyPowerPlan: false);
        Assert.NotEmpty(ids);
    }

    [Fact]
    public void Power_plan_appended_once()
    {
        var without = QuickOptimizePlan.Resolve("balanced", applyPowerPlan: false);
        var with = QuickOptimizePlan.Resolve("balanced", applyPowerPlan: true);
        Assert.Equal(without.Count + 1, with.Count);
        Assert.Equal(QuickOptimizePlan.PowerOverlayPerfId, with[^1]);
        Assert.Equal(1, with.Count(id => id == QuickOptimizePlan.PowerOverlayPerfId));
    }

    [Fact]
    public void Power_plan_not_duplicated_when_preset_contains_it()
    {
        var ids = QuickOptimizePlan.Resolve("full", applyPowerPlan: true);
        Assert.Equal(1, ids.Count(id => id == QuickOptimizePlan.PowerOverlayPerfId));
    }

    [Fact]
    public void Unknown_preset_throws()
    {
        Assert.Throws<InvalidOperationException>(() => QuickOptimizePlan.Resolve("no-such-preset", applyPowerPlan: false));
    }
}
