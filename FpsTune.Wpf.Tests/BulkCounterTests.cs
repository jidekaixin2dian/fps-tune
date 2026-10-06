using System.Diagnostics;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class BulkCounterTests
{
    private static CounterSample Sample(long raw, long time)
        => new(raw, 0, 10_000_000, 10_000_000, time, time, PerformanceCounterType.Timer100Ns);

    [Fact]
    public void New_instances_need_baselines_but_existing_instances_keep_rates()
    {
        var first = new Dictionary<string, CounterSample> { ["a"] = Sample(10, 100) };
        Assert.Empty(GpuCounterMath.CalculateRates(first, new Dictionary<string, CounterSample>()));
        var next = new Dictionary<string, CounterSample> { ["a"] = Sample(30, 200), ["b"] = Sample(10, 200) };
        var rate = Assert.Single(GpuCounterMath.CalculateRates(next, first));
        Assert.Equal("a", rate.Instance);
        Assert.Equal(20, rate.Value, 3);
    }

    [Fact]
    public void Disappeared_instances_are_not_reported_as_stale_values()
    {
        var previous = new Dictionary<string, CounterSample> { ["a"] = Sample(10, 100) };
        var current = new Dictionary<string, CounterSample> { ["b"] = Sample(30, 200) };
        Assert.Empty(GpuCounterMath.CalculateRates(current, previous));
    }

    [Fact]
    public void Bulk_snapshot_preserves_gpu_aggregation_semantics()
    {
        const string name = "pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D";
        var previous = new Dictionary<string, CounterSample> { [name] = Sample(10, 100) };
        var current = new Dictionary<string, CounterSample> { [name] = Sample(30, 200) };
        Assert.Equal(20, GpuCounterMath.AggregateGpuUtilization(GpuCounterMath.CalculateRates(current, previous)));
    }
}
