using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 显卡遥测：nvidia-smi 解析（含 [N/A] 与十六进制降频掩码）与判读优先级。
/// 数值全部来自真机一次真实输出，不编造字段。
/// </summary>
public class GpuTelemetryTests
{
    private const string RealLine =
        "NVIDIA GeForce RTX 5070 Ti Laptop GPU, 0, 3630, 12227, 54, 22.31, 115.00, 1575, 14001, [N/A], 0x0000000000000000";

    [Fact]
    public void Parses_a_real_nvidia_smi_line_and_keeps_unsupported_fields_null()
    {
        var reading = GpuTelemetry.Parse(RealLine);
        Assert.NotNull(reading);
        Assert.Equal("NVIDIA GeForce RTX 5070 Ti Laptop GPU", reading!.Name);
        Assert.Equal(0, reading.UtilizationPercent);
        Assert.Equal(3630, reading.MemoryUsedMb);
        Assert.Equal(12227, reading.MemoryTotalMb);
        Assert.Equal(54, reading.TemperatureC);
        Assert.Equal(22.31, reading.PowerW!.Value, 2);
        Assert.Equal(115.0, reading.PowerLimitW!.Value, 2);
        Assert.Equal(1575, reading.ClockSmMhz);
        Assert.Equal(14001, reading.ClockMemMhz);
        Assert.Null(reading.FanPercent);
        Assert.Equal(0ul, reading.ThrottleMask);
        Assert.Equal(29.7, reading.MemoryPercent!.Value, 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NVIDIA, 0, 100")]
    [InlineData(", 0, 3630, 12227, 54, 22.31, 115.00, 1575, 14001, [N/A], 0x0")]
    public void Rejects_short_or_nameless_lines(string line)
        => Assert.Null(GpuTelemetry.Parse(line));

    [Fact]
    public void Throttle_masks_are_read_as_hex_or_decimal()
    {
        var line = RealLine.Replace("0x0000000000000000", "0x0000000000000044");
        var reading = GpuTelemetry.Parse(line)!;
        Assert.True(reading.ThermalThrottled);
        Assert.True(reading.PowerThrottled);

        var decimalMask = GpuTelemetry.Parse(RealLine.Replace("0x0000000000000000", "4"))!;
        Assert.False(decimalMask.ThermalThrottled);
        Assert.True(decimalMask.PowerThrottled);
    }

    [Fact]
    public void Vram_pressure_outranks_every_other_verdict()
    {
        var reading = GpuTelemetry.Parse(RealLine.Replace(", 3630, 12227,", ", 11900, 12227,"))!;
        Assert.True(reading.MemoryPercent > GpuTelemetry.VramCriticalPercent);
        Assert.Equal(GpuVerdict.VramCritical, GpuTelemetry.Evaluate(reading));

        var high = GpuTelemetry.Parse(RealLine.Replace(", 3630, 12227,", ", 11150, 12227,"))!;
        Assert.Equal(GpuVerdict.VramHigh, GpuTelemetry.Evaluate(high));
    }

    [Fact]
    public void Throttling_is_reported_before_utilization()
    {
        var thermal = GpuTelemetry.Parse(RealLine.Replace("0x0000000000000000", "0x40"))! with { UtilizationPercent = 99 };
        Assert.Equal(GpuVerdict.ThermalThrottle, GpuTelemetry.Evaluate(thermal));

        var power = GpuTelemetry.Parse(RealLine.Replace("0x0000000000000000", "0x4"))! with { UtilizationPercent = 99 };
        Assert.Equal(GpuVerdict.PowerThrottle, GpuTelemetry.Evaluate(power));
    }

    [Theory]
    [InlineData(99, GpuVerdict.GpuBound)]
    [InlineData(97, GpuVerdict.GpuBound)]
    [InlineData(90, GpuVerdict.GpuBusy)]
    [InlineData(85, GpuVerdict.GpuBusy)]
    [InlineData(60, GpuVerdict.Headroom)]
    [InlineData(5, GpuVerdict.Idle)]
    public void Utilization_maps_to_bound_busy_headroom_or_idle(double utilization, GpuVerdict expected)
    {
        var reading = GpuTelemetry.Parse(RealLine)! with { UtilizationPercent = utilization };
        Assert.Equal(expected, GpuTelemetry.Evaluate(reading));
    }

    [Fact]
    public void A_reading_without_utilization_is_unavailable_instead_of_guessed()
    {
        var reading = GpuTelemetry.Parse(RealLine)! with { UtilizationPercent = null };
        Assert.Equal(GpuVerdict.Unavailable, GpuTelemetry.Evaluate(reading));
    }

    [Fact]
    public void Every_verdict_has_a_verdict_and_advice_key()
    {
        foreach (var verdict in Enum.GetValues<GpuVerdict>())
        {
            Assert.StartsWith("Str.GpuVerdict", GpuTelemetry.VerdictKey(verdict));
            Assert.StartsWith("Str.GpuAdvice", GpuTelemetry.AdviceKey(verdict));
        }
    }
}
