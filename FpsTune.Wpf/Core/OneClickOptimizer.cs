using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>「一键优化」入口：按 catalog 的推荐预设应用，不额外加驱动覆盖。</summary>
public static class OneClickOptimizer
{
    public static Task<RunResult> ApplyAsync() => ApplyAsync(AppState.GamePath);

    public static Task<RunResult> ApplyAsync(string? gamePath)
        => NativeOptimizationEngine.ApplyPresetAsync("balanced", gamePath);
}
