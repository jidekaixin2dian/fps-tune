using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// GainVerdict：等量切段与三档判定边界（样本不足 / 效应过小 / 区间跨 0）。
/// 统计口径全部来自 AbAnalysis（Welch 95% 区间），本层只做切段与映射。
/// </summary>
public sealed class GainVerdictTests
{
    /// <summary>构造 n 个稳定段：均值 mean，正弦扰动保证各段五元组互异（避开重复段闸）。</summary>
    private static List<AbRound> MakeRounds(double mean, double jitter = 0.4, int n = GainVerdict.SegmentCount)
    {
        var rounds = new List<AbRound>(n);
        for (var i = 0; i < n; i++)
        {
            var avg = mean + jitter * Math.Sin(i * 1.9);
            var p99 = 1000.0 / avg * 1.6; // 1% Low 约为均值的 62%
            rounds.Add(new AbRound(Math.Round(avg, 2), Math.Round(1000.0 / p99, 2), Math.Round(p99, 2), 8 + i % 3, 600));
        }
        return rounds;
    }

    [Fact]
    public void BuildRounds_splits_frames_into_equal_segments()
    {
        // 60 FPS × 60 s = 3600 帧 → 6 段 × 600 帧，每段均值一致。
        var frames = Enumerable.Repeat(1000.0 / 60, 3600).ToList();
        var rounds = GainVerdict.BuildRounds(frames);

        Assert.Equal(6, rounds.Count);
        Assert.All(rounds, r => Assert.Equal(600, r.Frames));
        Assert.All(rounds, r => Assert.Equal(60, r.AvgFps, 1));
        Assert.All(rounds, r => Assert.True(r.IsValid));
    }

    [Fact]
    public void BuildRounds_last_segment_takes_remainder()
    {
        // 3601 帧 → 前 5 段各 600，尾段 601。
        var frames = Enumerable.Repeat(1000.0 / 60, 3601).ToList();
        var rounds = GainVerdict.BuildRounds(frames);

        Assert.Equal(6, rounds.Count);
        Assert.Equal(600, rounds[0].Frames);
        Assert.Equal(601, rounds[^1].Frames);
    }

    [Fact]
    public void BuildRounds_returns_empty_when_total_frames_below_minimum()
    {
        var frames = Enumerable.Repeat(1000.0 / 60, GainVerdict.MinTotalFrames - 1).ToList();
        Assert.Empty(GainVerdict.BuildRounds(frames));
    }

    [Fact]
    public void BuildRounds_marks_stutters_above_50ms()
    {
        var frames = Enumerable.Repeat(1000.0 / 60, 3590).ToList();
        frames.AddRange(Enumerable.Repeat(120.0, 10)); // 10 帧卡顿集中注入
        var rounds = GainVerdict.BuildRounds(frames);

        Assert.Equal(6, rounds.Count);
        Assert.Equal(10, rounds.Sum(r => r.Stutters));
    }

    [Fact]
    public void Evaluate_confirmed_gain_keeps()
    {
        var verdict = GainVerdict.Evaluate(MakeRounds(100), MakeRounds(105.5));

        Assert.Equal(GainDecision.Keep, verdict.Decision);
        Assert.Equal(AbAnalysisCodes.VerdictConfirmed, verdict.Analysis.Verdict);
        Assert.True(verdict.Analysis.Sufficient);
        Assert.InRange(verdict.Analysis.AvgDiffPct!.Value, 4.5, 6.5);
        Assert.NotNull(verdict.Analysis.AvgDiffLowerPct);
    }

    [Fact]
    public void Evaluate_negligible_effect_rolls_back()
    {
        // 效应过小：区间整体低于 2% 有用线 → 确认没有值得保留的收益。
        var verdict = GainVerdict.Evaluate(MakeRounds(100), MakeRounds(100.2));

        Assert.Equal(GainDecision.Rollback, verdict.Decision);
        Assert.Equal(AbAnalysisCodes.VerdictNegligible, verdict.Analysis.Verdict);
    }

    [Fact]
    public void Evaluate_uncertain_cross_zero_is_insufficient()
    {
        // 区间跨 0：改后段间波动大到淹没差异（均值差 ~1%，段间 sd ~2.9%）。
        var verdict = GainVerdict.Evaluate(MakeRounds(100), MakeRounds(101, jitter: 4));

        Assert.Equal(GainDecision.Insufficient, verdict.Decision);
        Assert.Equal(AbAnalysisCodes.VerdictUncertain, verdict.Analysis.Verdict);
        Assert.True(verdict.Analysis.AvgDiffLowerPct <= 0);
    }

    [Fact]
    public void Evaluate_harmful_change_rolls_back()
    {
        var verdict = GainVerdict.Evaluate(MakeRounds(100), MakeRounds(94));

        Assert.Equal(GainDecision.Rollback, verdict.Decision);
        Assert.Equal(AbAnalysisCodes.VerdictHarmful, verdict.Analysis.Verdict);
        Assert.True(verdict.Analysis.AvgDiffPct < 0);
    }

    [Fact]
    public void Evaluate_tradeoff_rolls_back()
    {
        // 平均升、p99 帧时间明显恶化 → tradeoff（安全回退线）。
        var worseTail = new List<AbRound>();
        for (var i = 0; i < 6; i++)
        {
            var avg = 105.0 + 0.5 * Math.Sin(i * 2.3);
            var p99 = 1000.0 / 100 * 1.6 * 1.10; // p99 帧时间恶化 ~10%
            worseTail.Add(new AbRound(Math.Round(avg, 2), Math.Round(1000.0 / p99, 2), Math.Round(p99, 2), 8 + i % 3, 600));
        }
        var verdict = GainVerdict.Evaluate(MakeRounds(100), worseTail);

        Assert.Equal(GainDecision.Rollback, verdict.Decision);
        Assert.Equal(AbAnalysisCodes.VerdictTradeoff, verdict.Analysis.Verdict);
    }

    [Fact]
    public void Evaluate_too_few_segments_is_insufficient()
    {
        // 样本不足：只有 2 个有效段（低于 AbThresholds.MinRounds）。
        var verdict = GainVerdict.Evaluate(MakeRounds(100, n: 2), MakeRounds(105, n: 2));

        Assert.Equal(GainDecision.Insufficient, verdict.Decision);
        Assert.Equal(AbAnalysisCodes.VerdictInsufficient, verdict.Analysis.Verdict);
        Assert.Contains(AbAnalysisCodes.WhyRoundsBase, verdict.Analysis.InsufficientReasons);
        Assert.Contains(AbAnalysisCodes.WhyRoundsTest, verdict.Analysis.InsufficientReasons);
    }

    [Fact]
    public void Evaluate_duplicate_segments_are_flagged_insufficient()
    {
        var identical = MakeRounds(100);
        var dup = Enumerable.Repeat(identical[0], 6).ToList();
        var verdict = GainVerdict.Evaluate(dup, MakeRounds(105));

        Assert.Equal(GainDecision.Insufficient, verdict.Decision);
        Assert.Contains(AbAnalysisCodes.WhyDuplicatesBase, verdict.Analysis.InsufficientReasons);
    }

    [Fact]
    public void FormatGain_shows_signed_percent_only()
    {
        var verdict = GainVerdict.Evaluate(MakeRounds(100), MakeRounds(105.5));
        var text = GainVerdict.FormatGain(verdict.Analysis);

        Assert.StartsWith("+", text);
        Assert.EndsWith("%", text);
        Assert.False(text.Contains("置信区间") || text.Contains("95%"), "置信区间文案由展示层（Str.GainCi）组装，纯逻辑层只给数字");
    }
}
