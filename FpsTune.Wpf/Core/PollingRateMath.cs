namespace FpsTune.Wpf.Core;

/// <summary>
/// 实测到的鼠标报告频率。
/// </summary>
/// <param name="Sufficient">样本是否够：不够时界面只提示再动一动，不给数字。</param>
/// <param name="Samples">参与计算的移动上报次数。</param>
/// <param name="MedianIntervalMs">相邻上报间隔的中位数（毫秒）。</param>
/// <param name="MeasuredHz">按间隔中位数换算出的频率（四舍五入到整数）。</param>
/// <param name="SnappedRate">落到界面支持档位上的值；偏离所有档位太远时为 null。</param>
public sealed record PollingRateReading(
    bool Sufficient,
    int Samples,
    double MedianIntervalMs,
    int MeasuredHz,
    int? SnappedRate);

/// <summary>
/// 报告频率的换算（纯函数，不碰硬件、不读设置）。
///
/// 为什么按「间隔中位数」算：消息队列抖动、系统偶尔排队、鼠标静止都会让个别间隔失真，
/// 均值会被一两个长间隔拉低，中位数不受影响。静止造成的大间隔（超过
/// <see cref="MaxIntervalMs"/>）直接丢弃，否则「停手两秒」会被算成 30 Hz。
/// </summary>
public static class PollingRateMath
{
    /// <summary>超过这个间隔就当成「手停下来了」：40 Hz 对应 25 毫秒，低于它的设备不存在。</summary>
    public const double MaxIntervalMs = 25;

    /// <summary>至少要有这么多次上报才给结论：太少时中位数会被单个抖动带跑。</summary>
    public const int MinSamples = 24;

    /// <summary>与档位的相对误差上限：超过就不硬套档位，如实报实测值。</summary>
    public const double SnapTolerance = 0.25;

    /// <summary>把一串相邻上报间隔（毫秒）换算成报告频率。</summary>
    public static PollingRateReading Analyze(IReadOnlyList<double>? intervalsMs)
    {
        var usable = (intervalsMs ?? [])
            .Where(interval => interval > 0 && interval <= MaxIntervalMs)
            .OrderBy(interval => interval)
            .ToArray();
        if (usable.Length < MinSamples)
            return new PollingRateReading(false, usable.Length, 0, 0, null);

        var median = usable[usable.Length / 2];
        if (usable.Length % 2 == 0) median = (usable[usable.Length / 2 - 1] + median) / 2d;
        var measured = (int)Math.Round(1000d / median, MidpointRounding.AwayFromZero);
        return new PollingRateReading(true, usable.Length, median, measured, NearestRate(measured));
    }

    /// <summary>最接近的界面档位（125/250/500/1000/2000/4000/8000）；误差过大返回 null。</summary>
    public static int? NearestRate(int measuredHz)
    {
        if (measuredHz <= 0) return null;
        var best = InputQueuePolicy.PollingRates
            .OrderBy(rate => Math.Abs(rate - measuredHz))
            .First();
        return Math.Abs(best - measuredHz) <= best * SnapTolerance ? best : null;
    }
}
