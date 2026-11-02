using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class DpcLatencyTests
{
    [Fact]
    public void Non_admin_gets_actionable_result_without_starting_etw()
    {
        var report = DpcLatencyService.Measure(5, isAdmin: false);
        Assert.False(report.Ok);
        Assert.False(string.IsNullOrWhiteSpace(report.Error));
        Assert.Empty(report.Drivers);
    }

    [Fact]
    public void Sample_collector_rolls_up_dpc_and_isr_by_driver_and_keeps_unknown()
    {
        var modules = new[]
        {
            new DpcLatencyService.KernelModule(0x1000, 0x1000, "a.sys"),
            new DpcLatencyService.KernelModule(0x3000, 0x1000, "b.sys"),
        };
        var collector = new DpcLatencyService.SampleCollector(modules);
        collector.AddDpc(0x1100, 1.5);
        collector.AddDpc(0x3100, 3.0);
        collector.AddIsr(0x3200, 0.5);
        collector.AddDpc(0x9999, 0.1);

        var report = collector.Build(10, 10);
        Assert.True(report.Ok);
        Assert.Equal(3, report.DpcCount);
        Assert.Equal(1, report.IsrCount);
        Assert.Equal(4.6, report.DpcTotalMs, 2);
        Assert.Equal(0.5, report.IsrTotalMs, 2);
        Assert.Equal("b.sys", report.Drivers[0].Module);
        Assert.Equal(2, report.Drivers[0].CombinedCount);
        Assert.Equal(3.5, report.Drivers[0].CombinedTotalMs, 2);
        Assert.Contains(report.Drivers, d => d.Module == "Unknown");
    }

    [Fact]
    public void Image_load_can_arrive_after_dpc_and_still_attributes_the_driver()
    {
        var collector = new DpcLatencyService.SampleCollector(Array.Empty<DpcLatencyService.KernelModule>());
        collector.AddDpc(0x8800, 1.25);           // 先到 DPC，此时还没有模块表
        collector.AddImage(0x8000, 0x2000, @"C:\Windows\System32\drivers\fixture.sys");
        var report = collector.Build(10, 10);
        Assert.Equal("fixture.sys", report.Drivers.Single().Module);
        Assert.Equal(1.25, report.Drivers.Single().DpcTotalMs, 2);
    }

    [Fact]
    public void Duration_outside_allowed_range_is_rejected_before_sampling()
    {
        var report = DpcLatencyService.Measure(1, isAdmin: true);
        Assert.False(report.Ok);
        Assert.Equal(0, report.DurationSeconds);
        Assert.False(string.IsNullOrWhiteSpace(report.Error));
    }
}
