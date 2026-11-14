using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>预算卡上单条数值：毫秒值 + 来源（measured 实测 / derived 推算 / unavailable 不可测）。</summary>
public sealed record LatencyBudgetItem(double? ValueMs, string Source)
{
    public static LatencyBudgetItem Measured(double ms) => new(ms, "measured");
    public static LatencyBudgetItem Derived(double ms) => new(ms, "derived");
    public static LatencyBudgetItem Unavailable => new(null, "unavailable");
}

/// <summary>延迟预算的三段结果。每段给出数值与来源，不可测的段如实标注，不把推算值伪装成实测。</summary>
public sealed record LatencyBudgetResult(
    LatencyBudgetItem InputInterval,
    LatencyBudgetItem InputQueue,
    LatencyBudgetItem FrameP50,
    LatencyBudgetItem FrameP99,
    LatencyBudgetItem GpuBusyP50,
    LatencyBudgetItem DisplayLatencyP50,
    LatencyBudgetItem DisplayLatencyP99,
    double? RefreshHz,
    bool RefreshMeasured,
    IReadOnlyList<string> AdviceKeys);

/// <summary>
/// 端到端延迟预算（0.5.0 M5，纯逻辑）：输入（报告率 × 队列深度）→ 帧生成（帧时间与 GPU 忙的
/// p50/p99）→ 显示（显示等待与刷新率）。
/// 来源标注：输入段由报告率与缓冲深度推算（报告率可以是实测回填值）；帧生成/显示等待来自
/// 采样 CSV 的多列解析（实测）；刷新率来自系统显示模式（实测）。缺失列对应段「不可测」。
/// </summary>
public static class LatencyBudget
{
    public static LatencyBudgetResult Evaluate(FrameColumnSeries? series, int pollingRateHz, int queueDepth, double? refreshHz)
    {
        var advice = new List<string>();
        var rate = pollingRateHz > 0 ? pollingRateHz : 1000;
        var interval = 1000.0 / rate;
        var depth = queueDepth > 0 ? queueDepth : InputQueuePolicy.RecommendedDepth(rate);
        var inputInterval = LatencyBudgetItem.Derived(Math.Round(interval, 3));
        var inputQueue = LatencyBudgetItem.Derived(Math.Round(depth * interval, 2));

        // 输入段建议：缓冲深度低于推荐值时直接给跳转口径。
        if (InputQueuePolicy.IsBelowRecommended(depth, rate))
            advice.Add("Str.BudgetAdviceQueueDepth");
        if (rate <= 500)
            advice.Add("Str.BudgetAdvicePollingRate");

        // 帧生成段：全部来自采样列（实测）。
        double? frameP50 = null, frameP99 = null, gpuP50 = null, displayP50 = null, displayP99 = null;
        if (series is { } data && data.FrameTimesMs.Count > 0)
        {
            var sorted = data.FrameTimesMs.OrderBy(v => v).ToList();
            frameP50 = Percentile(sorted, 0.50);
            frameP99 = Percentile(sorted, 0.99);
        }
        if (series is { } d2 && d2.GpuBusyMs.Count > 0)
            gpuP50 = Percentile(d2.GpuBusyMs.OrderBy(v => v).ToList(), 0.50);
        if (series is { } d3 && d3.DisplayLatencyMs.Count > 0)
        {
            var sortedDisplay = d3.DisplayLatencyMs.OrderBy(v => v).ToList();
            displayP50 = Percentile(sortedDisplay, 0.50);
            displayP99 = Percentile(sortedDisplay, 0.99);
        }

        // 帧生成建议：GPU 忙占帧时间比例高 → 瓶颈在显卡，画质/DLSS 才有空间。
        if (gpuP50 is { } gpu && frameP50 is { } frame && frame > 0 && gpu / frame >= 0.85)
            advice.Add("Str.BudgetAdviceGpuBound");

        // 显示段建议：显示等待 p99 明显高于 p50 → 排队/合成抖动，检查全屏与帧率上限。
        if (displayP99 is { } p99 && displayP50 is { } p50 && p50 > 0 && p99 >= p50 * 2)
            advice.Add("Str.BudgetAdviceDisplayQueue");

        return new LatencyBudgetResult(
            inputInterval, inputQueue,
            frameP50 is { } f50 ? LatencyBudgetItem.Measured(Math.Round(f50, 2)) : LatencyBudgetItem.Unavailable,
            frameP99 is { } f99 ? LatencyBudgetItem.Measured(Math.Round(f99, 2)) : LatencyBudgetItem.Unavailable,
            gpuP50 is { } g50 ? LatencyBudgetItem.Measured(Math.Round(g50, 2)) : LatencyBudgetItem.Unavailable,
            displayP50 is { } d50 ? LatencyBudgetItem.Measured(Math.Round(d50, 2)) : LatencyBudgetItem.Unavailable,
            displayP99 is { } d99 ? LatencyBudgetItem.Measured(Math.Round(d99, 2)) : LatencyBudgetItem.Unavailable,
            refreshHz, refreshHz is > 0,
            advice);
    }

    /// <summary>最近秩百分位（与 FrameTimeStats 的 p99 口径一致）。</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * fraction) - 1, 0, sorted.Count - 1)];

    /// <summary>当前主显示器刷新率（Windows 枚举的当前显示模式）；读不到返回 null（显示段如实标注）。</summary>
    internal static double? ReadRefreshHz()
    {
        try
        {
            if (!new MscmsIccApi().TryGetPrimaryDisplay(out var display)) return null;
            return new WindowsDisplayModeApi().Read(display).Hz;
        }
        catch { return null; }
    }
}
