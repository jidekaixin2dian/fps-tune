using System.Windows;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 「滤镜在游戏里到底有没有效果」的契约：
/// 1) 滤镜曲线必须真的改变 gamma ramp（游戏只认 ramp，不读 ICC 关联）；
/// 2) 恒等曲线（没有滤镜）不能改变 ramp 一个字节；
/// 3) 游戏内效果通道的预览必须跟着曲线变——否则选完滤镜预览没反应，用户就会说"滤镜无效"。
/// </summary>
[Collection("BackupService serial")]
public sealed class FilterCurveRampTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    private static ushort[] Identity() => Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();

    [Fact]
    public void Filter_curve_changes_the_screen_ramp_and_identity_does_not()
    {
        var settings = new ScreenColorSettings();
        var plain = ScreenColorService.BuildRamp(settings, Identity());
        var identity = ScreenColorService.BuildRamp(settings, Identity(), IccCurves.Identity);
        Assert.Equal(plain, identity);

        var curves = IccCurveReader.TryRead(IccProfileGenerator.Build(IccFilterPreset.Vivid));
        Assert.NotNull(curves);
        var filtered = ScreenColorService.BuildRamp(settings, Identity(), curves);
        Assert.NotEqual(plain, filtered);

        // 曲线单调 → ramp 也应当单调不降（防止出现色带反折）。
        for (var c = 0; c < 3; c++)
            for (var i = 1; i < 256; i++)
                Assert.True(filtered[c * 256 + i] >= filtered[c * 256 + i - 1] - 1,
                    $"通道 {c} 第 {i} 点回折");
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
    public void Game_channel_preview_changes_when_a_filter_curve_is_set()
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

                var curves = IccCurveReader.TryRead(IccProfileGenerator.Build(IccFilterPreset.Vivid));
                Assert.NotNull(curves);
                var rev = preview.RenderedRevision;
                preview.SetFilterCurves(curves);
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > rev);
                var after = Hash(preview);

                Assert.NotEqual(before, after);
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
