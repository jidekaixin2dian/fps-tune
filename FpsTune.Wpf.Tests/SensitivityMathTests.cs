using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>灵敏度换算（参考实现的灵敏度调校助手）：用公开的 yaw 常量核对已知档位。</summary>
public class SensitivityMathTests
{
    [Theory]
    [InlineData(800, 1.0, 0.022, 51.95)]   // CS2 800 DPI @1.0 ≈ 51.95 cm/360
    [InlineData(800, 0.4, 0.07, 40.82)]    // VALORANT 800 DPI @0.4 ≈ 40.82 cm/360
    [InlineData(400, 2.0, 0.022, 51.95)]   // 半 DPI 双灵敏度 = 同一手感
    public void Computes_cm_per_360_with_the_published_yaw(double dpi, double sens, double yaw, double expected)
        => Assert.Equal(expected, SensitivityMath.Cm360(dpi, sens, yaw)!.Value, 1);

    [Fact]
    public void Edpi_is_dpi_times_sensitivity()
        => Assert.Equal(800, SensitivityMath.Edpi(800, 1.0)!.Value, 3);

    [Fact]
    public void Equivalent_sensitivity_keeps_the_same_turn_distance()
    {
        // CS2 800@1.0 与 VALORANT 800@? 的手感一致
        var valorant = SensitivityMath.Equivalent(1.0, 0.022, 0.07)!.Value;
        Assert.Equal(0.3143, valorant, 3);
        var a = SensitivityMath.Cm360(800, 1.0, 0.022);
        var b = SensitivityMath.Cm360(800, valorant, 0.07);
        Assert.Equal(a!.Value, b!.Value, 3);
    }

    [Theory]
    [InlineData(0, 1.0, 0.022)]
    [InlineData(800, 0, 0.022)]
    [InlineData(800, 1.0, 0)]
    [InlineData(-800, 1.0, 0.022)]
    public void Rejects_non_positive_inputs(double dpi, double sens, double yaw)
        => Assert.Null(SensitivityMath.Cm360(dpi, sens, yaw));
}
