using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 报告率换算：中位数抗抖动、静止间隔丢弃、档位吸附与「样本不足」判定。
/// </summary>
public class PollingRateMathTests
{
    private static double[] Intervals(double milliseconds, int count = 200)
        => Enumerable.Repeat(milliseconds, count).ToArray();

    [Theory]
    [InlineData(8.0, 125)]
    [InlineData(4.0, 250)]
    [InlineData(2.0, 500)]
    [InlineData(1.0, 1000)]
    [InlineData(0.5, 2000)]
    [InlineData(0.25, 4000)]
    [InlineData(0.125, 8000)]
    public void Common_intervals_snap_to_the_supported_rate(double interval, int expected)
    {
        var reading = PollingRateMath.Analyze(Intervals(interval));
        Assert.True(reading.Sufficient);
        Assert.Equal(expected, reading.SnappedRate);
        Assert.InRange(Math.Abs(reading.MeasuredHz - expected), 0, expected * 0.05);
    }

    [Fact]
    public void Idle_gaps_are_dropped_instead_of_dragging_the_result_down()
    {
        var intervals = new List<double>();
        intervals.AddRange(Intervals(1.0, 100));
        intervals.AddRange(Intervals(600, 30));   // 手停了 0.6 秒
        intervals.AddRange(Intervals(1.0, 100));

        var reading = PollingRateMath.Analyze(intervals);

        Assert.True(reading.Sufficient);
        Assert.Equal(1000, reading.SnappedRate);
        Assert.Equal(200, reading.Samples);
    }

    [Fact]
    public void A_handful_of_samples_is_reported_as_insufficient()
    {
        var reading = PollingRateMath.Analyze(Intervals(1.0, PollingRateMath.MinSamples - 1));
        Assert.False(reading.Sufficient);
        Assert.Null(reading.SnappedRate);
        Assert.Equal(0, reading.MeasuredHz);
    }

    [Fact]
    public void Null_or_empty_input_never_throws()
    {
        Assert.False(PollingRateMath.Analyze(null).Sufficient);
        Assert.False(PollingRateMath.Analyze([]).Sufficient);
    }

    [Fact]
    public void Rates_far_from_the_presets_report_the_measurement_instead_of_forcing_a_bucket()
    {
        // 3 毫秒 ≈ 333 Hz：离 250 与 500 都超过 25%，不硬套档位。
        var reading = PollingRateMath.Analyze(Intervals(3.0));
        Assert.True(reading.Sufficient);
        Assert.Null(reading.SnappedRate);
        Assert.InRange(reading.MeasuredHz, 320, 340);
    }

    [Fact]
    public void Median_ignores_a_few_outlier_intervals()
    {
        var intervals = new List<double>(Intervals(1.0, 200)) { 12, 18, 22 };
        var reading = PollingRateMath.Analyze(intervals);
        Assert.Equal(1000, reading.SnappedRate);
        Assert.Equal(1.0, reading.MedianIntervalMs, 3);
    }

    [Fact]
    public void Jitter_inside_the_tolerance_still_lands_on_the_rate()
    {
        var random = new Random(20261113);
        var intervals = Enumerable.Range(0, 500).Select(_ => 1.0 + (random.NextDouble() - 0.5) * 0.2).ToArray();
        var reading = PollingRateMath.Analyze(intervals);
        Assert.Equal(1000, reading.SnappedRate);
    }

    [Fact]
    public void Nearest_rate_never_invents_a_bucket_for_zero()
    {
        Assert.Null(PollingRateMath.NearestRate(0));
        Assert.Null(PollingRateMath.NearestRate(-5));
        Assert.Equal(125, PollingRateMath.NearestRate(120));
        Assert.Equal(8000, PollingRateMath.NearestRate(7900));
    }
}
