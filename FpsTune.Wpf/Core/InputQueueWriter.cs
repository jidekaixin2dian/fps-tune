using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 把维护页选定的键鼠缓冲深度落到系统：先把「想要的深度」写进设置，再走既有优化引擎，
/// 因此写前备份、逐项回执、写后读回复核、可还原都沿用同一条路径，不另起一套。
/// </summary>
internal static class InputQueueWriter
{
    /// <summary>键鼠缓冲对应的两个优化项 id（顺序与结果顺序一致）。</summary>
    internal static readonly string[] ItemIds = ["keyboard-latency", "mouse-latency"];

    /// <summary>测试注入：替换「交给引擎落盘」这一步，设置仍然照常保存。</summary>
    internal static Func<int, int, Task<IReadOnlyList<OptimizationApplyResult>>>? WriteOverride { get; set; }

    internal static async Task<IReadOnlyList<OptimizationApplyResult>> WriteAsync(
        int keyboardDepth, int mouseDepth, int pollingRate)
    {
        var settings = SettingsService.Current;
        settings.KeyboardQueueDepth = InputQueuePolicy.Normalize(keyboardDepth);
        settings.MouseQueueDepth = InputQueuePolicy.Normalize(mouseDepth);
        settings.MousePollingRate = pollingRate;
        SettingsService.Save(settings);

        if (WriteOverride is { } custom)
            return await custom(settings.KeyboardQueueDepth, settings.MouseQueueDepth);

        // 引擎按设置里的深度写注册表，并在写后读回；这里不做第二套写入。
        var receipt = await OptimizationEngine.ApplyItemsWithReceiptAsync(ItemIds, null);
        return receipt.Results;
    }
}
