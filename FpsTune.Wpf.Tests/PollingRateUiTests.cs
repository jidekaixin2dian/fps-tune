using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 维护页「实测报告率」的真界面用例：注入读数，核对按钮与状态行，
/// 并渲染深浅两套主题（最小窗口）留证。不注入真实鼠标事件。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class PollingRateUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "startup-audit", "ui");

    [Fact]
    public void Measured_rate_enables_using_it_and_explains_a_thin_sample()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(PollingRateUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var original = MousePollingMeter.MeasureOverride;
            try
            {
                var view = new MaintenanceView();
                var window = new Window { Width = 1180, Height = 720, Content = view };
                DlssFeedbackTests.PrepareBackgroundWindow(window);
                window.Show();
                window.UpdateLayout();
                try
                {
                    view.InputNav.IsChecked = true;
                    window.UpdateLayout();

                    // 1) 样本不足：不给数字，也不允许套用。
                    MousePollingMeter.MeasureOverride = _ => Task.FromResult(
                        new PollingRateReading(false, 5, 0, 0, null));
                    view.MeasurePollingAsync().GetAwaiter().GetResult();
                    Assert.Equal(Str.T("Str.InputQueueMeasureTooFew", PollingRateMath.MinSamples), view.PollingMeasureStatus.Text);
                    Assert.False(view.PollingUseButton.IsEnabled);

                    // 2) 实测 1000 Hz：给出档位并允许套用。
                    MousePollingMeter.MeasureOverride = _ => Task.FromResult(
                        new PollingRateReading(true, 3000, 1.0, 1000, 1000));
                    view.MeasurePollingAsync().GetAwaiter().GetResult();
                    Assert.Equal(Str.T("Str.InputQueueMeasureResult", 1000, 3000, "1.00"), view.PollingMeasureStatus.Text);
                    Assert.True(view.PollingUseButton.IsEnabled);

                    // 3) 套用后下拉跟着变，读数按新频率刷新。
                    view.PollingUseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert.Equal(Str.T("Str.InputQueueMeasureApplied", 1000), view.PollingMeasureStatus.Text);
                    Assert.Equal(1000, SettingsService.Current.MousePollingRate);
                    Assert.Equal(1000, view.SelectedPollingRate);

                    foreach (var theme in new[] { "light", "dark" })
                    {
                        SettingsService.Current.ThemeMode = theme;
                        ThemeManager.SetMode(theme);
                        window.UpdateLayout();
                        Render(view, "input-polling-" + theme);
                    }
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                MousePollingMeter.MeasureOverride = original;
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
