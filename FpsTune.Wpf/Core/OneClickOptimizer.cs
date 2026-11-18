using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>「一键优化」入口：按 catalog 的推荐预设应用，不额外加驱动覆盖。</summary>
public static class OneClickOptimizer
{
    internal static Task<NativeOptimizationEngine.ApplyReceipt> ApplyAsync() => ApplyAsync(AppState.GamePath);

    internal static Task<NativeOptimizationEngine.ApplyReceipt> ApplyAsync(string? gamePath)
        => NativeOptimizationEngine.ApplyItemsWithReceiptAsync(OptimizationCatalog.ResolvePreset("balanced"), gamePath);
}
