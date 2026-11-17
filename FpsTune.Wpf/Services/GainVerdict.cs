using System.Globalization;

namespace FpsTune.Wpf.Services;

/// <summary>对照结论的三档判定（用户动作导向）。</summary>
public enum GainDecision
{
    /// <summary>统计确认有提升且达到有用线：保留改动。</summary>
    Keep,
    /// <summary>明确变差 / 有取舍 / 确认没有收益：还原改动。</summary>
    Rollback,
    /// <summary>样本不足或噪声淹没差异：不出结论，由用户决定。</summary>
    Insufficient,
}

/// <summary>对照判定结果。数值全部来自 <see cref="StatisticsResult"/>（同一套统计口径），本层只做三档映射与切段。</summary>
public sealed record GainVerdictResult(
    GainDecision Decision,
    string ReasonKey,
    StatisticsResult Analysis,
    int SegmentsBefore,
    int SegmentsAfter)
{
    /// <summary>方法口径说明（结论卡展示）：统计单位是采样窗口内的等量时间段，不是逐帧。</summary>
    public const string MethodNote = "segment-welch-t-95ci";
}

/// <summary>
/// 前后对照的收益判定（纯逻辑，0.5.0 M2）。
///
/// 统计单位：一次 60 s 采样内部切成 6 个等量时间段（约 10 s/段），每段汇总成一个
/// <see cref="SegmentSample"/>（平均帧 / 1% Low / p99 帧时间 / 卡顿），再交给既有的
/// <see cref="SampleStatistics.Compare"/>（Welch 95% 区间）。段独立性弱于独立录制轮次，
/// 结论卡必须如实标注「按 10 秒段估计」；需要更硬的证据时用同一设置再跑一次对照。
///
/// 三档映射（全部复用 SampleStatistics 的判定，不另设阈值）：
/// - confirmed（区间整体在 0 以上且上界达到 2% 有用线）→ 值得保留；
/// - harmful / tradeoff / negligible（明确变差、尾部恶化、确认无收益）→ 建议回滚；
/// - uncertain / insufficient（区间跨 0、样本不足、重复或离群）→ 证据不足。
/// </summary>
public static class GainVerdict
{
    /// <summary>一次采样切成的段数（60 s → 6 段）。</summary>
    public const int SegmentCount = 6;

    /// <summary>每段最少帧数：低于它的段不参与统计（过低帧率下的稀疏段没有统计意义）。</summary>
    public const int MinFramesPerSegment = 100;

    /// <summary>有效段数下限沿用 StatisticsThresholds.MinRounds（3）；切不出足够段按样本不足处理。</summary>
    public const int MinTotalFrames = SegmentCount * MinFramesPerSegment;

    /// <summary>
    /// 把逐帧帧时间切成等量段并汇总成统计轮次。总量不足时返回不足 <see cref="SegmentCount"/> 个
    /// 段（可能为空），由 <see cref="Evaluate"/> 判证据不足。
    /// </summary>
    public static List<SegmentSample> BuildRounds(IReadOnlyList<double> frameTimesMs, int segmentCount = SegmentCount)
    {
        var rounds = new List<SegmentSample>(segmentCount);
        if (segmentCount <= 0 || frameTimesMs.Count < MinFramesPerSegment)
            return rounds;
        var per = frameTimesMs.Count / segmentCount;
        if (per < MinFramesPerSegment)
            return rounds; // 段太稀疏：整体按样本不足处理，不硬凑。
        for (var s = 0; s < segmentCount; s++)
        {
            var start = s * per;
            var count = s == segmentCount - 1 ? frameTimesMs.Count - start : per;
            if (count < MinFramesPerSegment)
                break;
            var slice = new List<double>(count);
            for (var i = start; i < start + count; i++)
                slice.Add(frameTimesMs[i]);
            // 口径与 FrameTimeStats.Parse 一致：AvgFps=1000/平均帧时间、P1Low=1000/p99、卡顿=帧时间>50ms。
            var avgMs = slice.Average();
            slice.Sort();
            var p99Ms = slice[Math.Clamp((int)Math.Ceiling(slice.Count * 0.99) - 1, 0, slice.Count - 1)];
            var stutters = slice.Count(v => v > 50);
            rounds.Add(new SegmentSample(
                Math.Round(1000.0 / avgMs, 2),
                Math.Round(1000.0 / p99Ms, 2),
                Math.Round(p99Ms, 2),
                stutters,
                count));
        }
        return rounds;
    }

    /// <summary>三档判定。两侧段数各自沿用既有闸（重复 / 离群 / CV / 条件一致）。</summary>
    public static GainVerdictResult Evaluate(IReadOnlyList<SegmentSample> before, IReadOnlyList<SegmentSample> after)
    {
        // 对照两侧条件天然一致（同游戏同场景同时长同采样器），条件字段按相同值传入，
        // 不触发 SampleStatistics 的 condition-mismatch 闸。
        var analysis = SampleStatistics.Compare(
            new StatisticsInput("before", before, "capture", 60, "comparison", "comparison"),
            new StatisticsInput("after", after, "capture", 60, "comparison", "comparison"));
        var (decision, reasonKey) = analysis.Verdict switch
        {
            VerdictCodes.VerdictConfirmed => (GainDecision.Keep, "Str.GainVerdictKeep"),
            VerdictCodes.VerdictTradeoff => (GainDecision.Rollback, "Str.GainVerdictTradeoff"),
            VerdictCodes.VerdictHarmful => (GainDecision.Rollback, "Str.GainVerdictHarmful"),
            VerdictCodes.VerdictNegligible => (GainDecision.Rollback, "Str.GainVerdictNegligible"),
            VerdictCodes.VerdictUncertain => (GainDecision.Insufficient, "Str.GainVerdictUncertain"),
            _ => (GainDecision.Insufficient, "Str.GainVerdictInsufficient"),
        };
        return new GainVerdictResult(decision, reasonKey, analysis, before.Count, after.Count);
    }

    /// <summary>增益百分点（保留两位；基线均值非正返回 null）。</summary>
    public static double? DiffPercent(StatisticsResult analysis) => analysis.AvgDiffPct;

    /// <summary>增益文案（结论卡）：+12.3%；无区间数据时为 —。置信区间文案由展示层组装（Str.GainCi）。</summary>
    public static string FormatGain(StatisticsResult analysis)
    {
        if (analysis.AvgDiffPct is not { } diff)
            return "—";
        return Format(diff);

        static string Format(double value)
            => (value >= 0 ? "+" : "") + value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }
}
