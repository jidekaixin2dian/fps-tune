using System.Windows;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 「滤镜在游戏里到底有没有效果」的契约（2026-11-03 语义修正后）：
/// 1) 只有 vcgt 校准表（编码值→编码值）才折进 gamma ramp；
/// 2) 生成式预设的 rTRC 是「编码→线性」的显示响应（MapTone 的反函数），折进去会与画面参数抵消，
///    表现是「所有滤镜看着一样、整体还发暗」——所以必须原样放过；
/// 3) 不同预设写屏后的曲线必须彼此不同（否则用户就是在换名字）；
/// 4) 恒等曲线、没有滤镜时都不许改变 ramp 一个字节。
/// </summary>
[Collection("BackupService serial")]
public sealed class FilterCurveRampTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    private static ushort[] Identity() => Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();

    private static IccCurves CalibrationRamp(double gamma)
    {
        var channel = Enumerable.Range(0, 256).Select(i => Math.Pow(i / 255d, gamma)).ToArray();
        return IccCurves.FromCurves([channel, channel, channel], true, false, encodedRamp: true);
    }

    [Fact]
    public void Only_a_calibration_ramp_folds_into_the_screen_curve()
    {
        var settings = new ScreenColorSettings();
        var plain = ScreenColorService.BuildRamp(settings, Identity());
        Assert.Equal(plain, ScreenColorService.BuildRamp(settings, Identity(), IccCurves.Identity));

        // 内置预设现在自带 vcgt（对齐参考实现）。它折进 ramp 会改变画面——但应用路径**不折**它，
        // 因为预设的观感已经由画面参数（ForPreset）表达，两边都折 = 套两遍。
        var presetCurves = IccCurveReader.TryRead(IccProfileGenerator.Build(IccFilterPreset.Vivid));
        Assert.NotNull(presetCurves);
        Assert.True(presetCurves!.EncodedRamp);
        var presetTone = ScreenColorSettings.ForPreset(IccFilterPreset.Vivid);
        var once = ScreenColorService.BuildRamp(presetTone, Identity());
        var twice = ScreenColorService.BuildRamp(presetTone, Identity(), presetCurves);
        Assert.NotEqual(once, twice);

        // 外部 .icc 的校准表才折入，而且要单调不降。
        var encoded = CalibrationRamp(0.8);
        Assert.True(encoded.EncodedRamp);
        var filtered = ScreenColorService.BuildRamp(settings, Identity(), encoded);
        Assert.NotEqual(plain, filtered);
        for (var c = 0; c < 3; c++)
            for (var i = 1; i < 256; i++)
                Assert.True(filtered[c * 256 + i] >= filtered[c * 256 + i - 1] - 1,
                    $"通道 {c} 第 {i} 点回折");
    }

    [Fact]
    public void Different_presets_write_different_screen_curves()
    {
        // 这是「所有 ICC 滤镜看着区别不大」的回归守卫：每个预设写进屏幕的曲线必须两两不同。
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var preset in Enum.GetValues<IccFilterPreset>())
        {
            var ramp = ScreenColorService.BuildRamp(
                ScreenColorSettings.ForPreset(preset), Identity(),
                IccCurveReader.TryRead(IccProfileGenerator.Build(preset)));
            var key = string.Join(',', Enumerable.Range(0, 768).Where(i => i % 16 == 0).Select(i => ramp[i]));
            Assert.False(seen.ContainsKey(key), $"{preset} 与 {seen.GetValueOrDefault(key)} 写屏后完全相同");
            seen[key] = preset.ToString();
        }
    }

    [Fact]
    public void SampleCurve_matches_the_underlying_table()
    {
        var table = Enumerable.Range(0, 256).Select(i => Math.Pow(i / 255d, 0.8)).ToArray();
        Assert.Equal(0, ScreenColorService.SampleCurve(table, 0), 6);
        Assert.Equal(1, ScreenColorService.SampleCurve(table, 1), 6);
        Assert.Equal(table[128], ScreenColorService.SampleCurve(table, 128 / 255d), 6);
        // 端点外按夹紧处理
        Assert.Equal(0, ScreenColorService.SampleCurve(table, -1), 6);
        Assert.Equal(1, ScreenColorService.SampleCurve(table, 2), 6);
    }

    [Fact]
    public void Game_channel_preview_follows_a_calibration_ramp()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(FilterCurveRampTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var preview = new ColorPreviewControl();
            var window = new Window { Width = 640, Height = 420, Content = preview, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -4000, Top = -4000 };
            app.MainWindow = window;
            try
            {
                window.Show();
                DlssFeedbackTests.PumpUntil(() => preview.IsLoaded);
                DlssFeedbackTests.PumpUntil(() => preview.RenderedSource is not null);
                preview.SetSettings(new ScreenColorSettings());
                System.Threading.Thread.Sleep(150);
                var before = Hash(preview);

                // 校准表（vcgt 型）必须能改变预览；没有曲线时不许变。
                var rev = preview.RenderedRevision;
                preview.SetFilterCurves(CalibrationRamp(1.6));
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > rev);
                var after = Hash(preview);
                Assert.NotEqual(before, after);

                rev = preview.RenderedRevision;
                preview.SetFilterCurves(IccCurves.Identity);
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > rev);
                Assert.Equal(before, Hash(preview));
            }
            finally { window.Content = null; }
        });
    }

    private static long Hash(ColorPreviewControl preview)
    {
        var w = (int)Math.Round(Math.Max(1, preview.ActualWidth));
        var h = (int)Math.Round(Math.Max(1, preview.ActualHeight));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(preview);
        var pixels = new byte[w * h * 4];
        bmp.CopyPixels(pixels, w * 4, 0);
        long sum = 0;
        unchecked { for (var i = 0; i < pixels.Length; i += 7) sum = sum * 31 + pixels[i]; }
        return sum;
    }
}
