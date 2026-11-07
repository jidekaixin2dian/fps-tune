using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 暗部平衡（反汇编还原的暗部映射）与新接入的调色预设。
/// 曲线必须能逐点复算：p→12 档 γ→(i+1)/256 幂律，黑位抬起、白点锁 65535。
/// </summary>
public class DarkBalanceFilterTests
{
    private static ushort[] Identity() => Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();

    /// <summary>按逆向出的公式直接复算，作为我们实现的对照（不调用产品代码）。</summary>
    private static ushort[] GamePpRamp(double p)
    {
        var step = (int)(p / 100 * 11 + 26);
        var gamma = (40 - step) / 10d;
        var ramp = new ushort[256];
        for (var i = 0; i < 256; i++)
            ramp[i] = (ushort)Math.Truncate(Math.Clamp(Math.Pow((i + 1) / 256d, gamma), 0, 1) * 65535 + 0.5);
        return ramp;
    }

    [Theory]
    [InlineData(0, 1.4)]
    [InlineData(45, 1.0)]
    [InlineData(50, 0.9)]
    [InlineData(100, 0.3)]
    public void Maps_slider_to_the_same_twelve_step_gamma(double value, double expected)
        => Assert.Equal(expected, ScreenColorService.DarkBalanceGamma(value), 3);

    [Fact]
    public void Only_twelve_gamma_steps_are_reachable()
    {
        var gammas = Enumerable.Range(0, 101).Select(v => Math.Round(ScreenColorService.DarkBalanceGamma(v), 3)).Distinct().ToList();
        Assert.Equal(12, gammas.Count);
        Assert.Equal(1.4, gammas.Max());
        Assert.Equal(0.3, gammas.Min());
        Assert.Contains(1.0, gammas);
    }

    [Fact]
    public void Neutral_value_leaves_every_parameter_untouched()
    {
        var settings = new ScreenColorSettings(Brightness: 3, Contrast: 107, Gamma: 1.12, Shadows: 6, Red: 102, Green: 99, Blue: 97, Highlight: -8,
            DarkBalance: ScreenColorService.NeutralDarkBalance, DarkBalanceEnabled: true);
        var legacy = Enumerable.Range(0, 256)
            .Select(i => LegacyTone(i / 255d, settings))
            .ToArray();
        for (var i = 0; i < 256; i += 7)
            Assert.Equal(legacy[i], ScreenColorService.MapTone(i / 255d, settings, 100), 6);
    }

    [Fact]
    public void Ramp_matches_the_reference_formula_within_one_percent()
    {
        foreach (var p in new double[] { 0, 20, 45, 50, 100 })
        {
            var ours = ScreenColorService.BuildRamp(new ScreenColorSettings(DarkBalance: p, DarkBalanceEnabled: true), Identity());
            var reference = GamePpRamp(p);
            for (var i = 0; i < 256; i++)
            {
                var delta = Math.Abs(ours[i] - reference[i]);
                Assert.True(delta <= 700, $"p={p} i={i} ours={ours[i]} ref={reference[i]}");
            }
        }
    }

    [Fact]
    public void Reference_default_lifts_black_and_keeps_white()
    {
        var ramp = ScreenColorService.BuildRamp(new ScreenColorSettings(DarkBalance: 50, DarkBalanceEnabled: true), Identity());
        // 参考实现默认档（p=50, γ=0.9）实测 ramp[0] ≈ 446，白点保持 65535。
        Assert.InRange(ramp[0], 420, 470);
        Assert.Equal(65535, ramp[255]);
        Assert.Equal(ramp[0], ramp[256]);
        Assert.Equal(ramp[0], ramp[512]);
    }

    [Fact]
    public void Extreme_values_are_monotonic_and_ordered()
    {
        // 值越大 γ 越小 → 画面越亮（0 → γ1.4 压暗，100 → γ0.3 提亮）。
        var crushed = ScreenColorService.BuildRamp(new ScreenColorSettings(DarkBalance: 0, DarkBalanceEnabled: true), Identity());
        var lifted = ScreenColorService.BuildRamp(new ScreenColorSettings(DarkBalance: 100, DarkBalanceEnabled: true), Identity());
        for (var i = 1; i < 256; i++)
        {
            Assert.True(crushed[i] >= crushed[i - 1]);
            Assert.True(lifted[i] >= lifted[i - 1]);
        }
        Assert.True(lifted[128] > crushed[128]);
        Assert.True(lifted[0] > 0);
        Assert.True(crushed[0] > 0);
    }

    [Fact]
    public void Toggle_off_ignores_the_slider_value_entirely()
    {
        // 参考实现是「开关 + 滑杆」：开关关着时这条曲线完全不参与，滑杆只是记着。
        var off = ScreenColorService.BuildRamp(new ScreenColorSettings(DarkBalance: 100), Identity());
        var neutral = ScreenColorService.BuildRamp(
            new ScreenColorSettings(DarkBalance: ScreenColorService.NeutralDarkBalance), Identity());
        Assert.Equal(neutral, off);

        var on = ScreenColorService.BuildRamp(new ScreenColorSettings(DarkBalance: 100, DarkBalanceEnabled: true), Identity());
        Assert.NotEqual(neutral, on);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rejects_out_of_range_dark_balance(int value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenColorSettings(DarkBalance: value).Validate());

    [Fact]
    public void Every_built_in_preset_is_inside_our_ranges_and_generates_a_profile()
    {
        foreach (var preset in Enum.GetValues<IccFilterPreset>())
        {
            var settings = ScreenColorSettings.ForPreset(preset);
            settings.Validate();
            var bytes = IccProfileGenerator.Build(preset);
            Assert.True(bytes.Length > 128, preset.ToString());
            Assert.False(string.IsNullOrWhiteSpace(ScreenColorSettings.TitleFor(preset)));
        }
    }

    [Fact]
    public void Newly_added_presets_keep_their_reference_directions()
    {
        // 白天=暗部全开、夜晚=更暗更浓、明透2.0=压高光，三条方向不能写反。
        Assert.True(ScreenColorSettings.ForPreset(IccFilterPreset.Daylight).Shadows >= 14);
        Assert.True(ScreenColorSettings.ForPreset(IccFilterPreset.NightScene).Contrast >= 110);
        Assert.True(ScreenColorSettings.ForPreset(IccFilterPreset.ClearBright2).Highlight < 0);
        Assert.True(ScreenColorSettings.ForPreset(IccFilterPreset.ArenaCool).Blue > ScreenColorSettings.ForPreset(IccFilterPreset.ArenaCool).Red);
    }

    [Theory]
    [InlineData(IccFilterPreset.Daylight, 75)]
    [InlineData(IccFilterPreset.VividLively, 90)]
    [InlineData(IccFilterPreset.ArenaCool, 95)]
    [InlineData(IccFilterPreset.GameR6, 32)]
    [InlineData(IccFilterPreset.GameCod16, 93)]
    public void Presets_carry_the_reference_digital_vibrance_value(IccFilterPreset preset, int expected)
        => Assert.Equal(expected, IccProfileGenerator.VibranceFor(preset));

    [Fact]
    public void Presets_without_a_reference_vibrance_stay_null()
    {
        Assert.Null(IccProfileGenerator.VibranceFor(IccFilterPreset.Vivid));
        Assert.Null(IccProfileGenerator.VibranceFor(IccFilterPreset.Neutral));
    }

    /// <summary>改造前的 MapTone（无条件 x^(1/Gamma)），用于证明中性值零回归。</summary>
    private static double LegacyTone(double x, ScreenColorSettings settings)
    {
        var y = Math.Pow(x, 1 / settings.Gamma);
        y += settings.Shadows / 100 * 4 * y * Math.Pow(1 - y, 2);
        y += (settings.Contrast / 100 - 1) * 2 * y * (1 - y) * (2 * y - 1);
        y += settings.Brightness / 100 * 4 * y * (1 - y);
        var w = y <= .5 ? 0 : (y - .5) * 2;
        w = w * w * (3 - 2 * w);
        y += settings.Highlight / 100 * w * y * (1 - y) * 2;
        return Math.Pow(Math.Clamp(y, 0, 1), 100 / 100);
    }
}
