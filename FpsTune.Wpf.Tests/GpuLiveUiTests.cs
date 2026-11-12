using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 显卡实时状态卡的真渲染用例：注入一份读数，核对八项数值与结论，
/// 再渲染深浅两套主题留证；读不到时只说明原因、不编数值。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class GpuLiveUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "gpu-live", "ui");

    [Fact]
    public void Gpu_panel_shows_metrics_and_verdict_in_both_themes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(GpuLiveUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var original = GpuTelemetryService.ReadOverride;
            try
            {
                var reading = new GpuReading("NVIDIA GeForce RTX 5070 Ti Laptop GPU", 99, 3630, 12227, 54, 22.31, 115, 1575, 14001, null, 0);
                GpuTelemetryService.ReadOverride = () => new GpuTelemetrySnapshot(
                    reading, GpuTelemetry.Evaluate(reading), DateTimeOffset.Now, null);

                var panel = new GpuLivePanel();
                var window = new Window { Width = 960, Height = 460, Content = panel };
                DlssFeedbackTests.PrepareBackgroundWindow(window);
                window.Show();
                window.UpdateLayout();
                try
                {
                    panel.Refresh();
                    window.UpdateLayout();

                    Assert.Equal("NVIDIA GeForce RTX 5070 Ti Laptop GPU", panel.GpuNameText.Text);
                    Assert.Equal("99%", panel.UtilText.Text);
                    Assert.Equal(Str.T("Str.GpuMemoryFormat", "3.5", "11.9"), panel.MemoryText.Text);
                    Assert.Equal("54 °C", panel.TempText.Text);
                    Assert.Equal(Str.T("Str.GpuLivePowerFormat", "22", "115"), panel.PowerText.Text);
                    Assert.Equal("1575 MHz", panel.ClockSmText.Text);
                    Assert.Equal("14001 MHz", panel.ClockMemText.Text);
                    Assert.Equal("—", panel.FanText.Text);
                    Assert.Equal(Str.T("Str.GpuLiveNoThrottle"), panel.ThrottleText.Text);
                    Assert.Equal(Str.T("Str.GpuVerdictBound"), panel.GpuVerdictText.Text);
                    Assert.Equal(Str.T("Str.GpuAdviceBound"), panel.AdviceText.Text);

                    foreach (var theme in new[] { "light", "dark" })
                    {
                        SettingsService.Current.ThemeMode = theme;
                        ThemeManager.SetMode(theme);
                        window.UpdateLayout();
                        Render(panel, "gpu-live-" + theme);
                    }
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                GpuTelemetryService.ReadOverride = original;
            }
        });
    }

    [Fact]
    public void Gpu_panel_reports_unavailable_without_inventing_numbers()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(GpuLiveUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var original = GpuTelemetryService.ReadOverride;
            try
            {
                GpuTelemetryService.ReadOverride = () =>
                    GpuTelemetrySnapshot.Unavailable(Str.T("Str.GpuUnavailableParse"));

                var panel = new GpuLivePanel();
                var window = new Window { Width = 960, Height = 460, Content = panel };
                DlssFeedbackTests.PrepareBackgroundWindow(window);
                window.Show();
                window.UpdateLayout();
                try
                {
                    panel.Refresh();
                    window.UpdateLayout();

                    Assert.Equal(Str.T("Str.GpuLiveNameUnavailable"), panel.GpuNameText.Text);
                    Assert.Equal(Str.T("Str.GpuVerdictUnavailable"), panel.GpuVerdictText.Text);
                    Assert.Equal("—", panel.UtilText.Text);
                    Assert.Equal("—", panel.MemoryText.Text);
                    Assert.Contains("格式", panel.StatusText.Text);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                GpuTelemetryService.ReadOverride = original;
            }
        });
    }

    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, element.ActualWidth, element.ActualHeight)
            }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        Directory.CreateDirectory(Output);
        using var stream = File.Create(Path.Combine(Output, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
