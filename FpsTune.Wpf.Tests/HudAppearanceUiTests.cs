using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class HudAppearanceUiTests
{
    [Fact]
    public void Global_hud_controls_preview_save_transparency_and_close_on_native_mouse_messages()
    {
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        { DlssFeedbackTests.RunIsolated(testType: typeof(HudAppearanceUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            using var session = new PerformanceSessionService((interval, capacity) => new MetricsSampler(interval, capacity,
                () => new(DateTime.Now, 42, 55, 64, 2UL << 30, 8UL << 30)), () => new SessionFpsRecorder(findPresentMon: () => null));
            using var metrics = new MetricsSampler(TimeSpan.FromSeconds(1), 60, () => new(DateTime.Now, 42, 55, 64, 2UL << 30, 8UL << 30));
            var controller = new PerformanceHudService(session, metrics, DlssFeedbackTests.PrepareBackgroundWindow);
            var main = new MainWindow { Width = 1180, Height = 720, HudController = controller }; app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show(); main.UpdateLayout();
            void Click(string name) => ((Button)main.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try
            {
                Assert.False(((ContentControl)main.FindName("PageHost")).Content is SessionView);
                Click("HudEntryButton"); Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("HudOptionsPanel")).Visibility);
                var hud = controller.Window!; DlssFeedbackTests.AssertBackgroundWindow(hud); metrics.SampleOnce();
                var slider = (Slider)main.FindName("HudTransparencySlider");
                foreach (var theme in new[] { "light", "dark" })
                foreach (var layout in new[] { "card", "strip" })
                foreach (var transparency in new[] { 0, 50, 100 })
                {
                    ThemeManager.SetMode(theme);
                    ((RadioButton)main.FindName(layout == "card" ? "HudCardOption" : "HudStripOption")).IsChecked = true;
                    slider.Value = transparency; hud.UpdateLayout();
                    Assert.Equal(layout == "strip" ? 560 : 300, hud.Width);
                    Assert.Equal(1 - transparency / 100d, ((FrameworkElement)hud.FindName("HudBackdrop")).Opacity);
                    Assert.Equal(1, ((TextBlock)hud.FindName("FpsText")).Opacity);
                    Assert.True(((Button)hud.FindName("CloseHudButton")).IsVisible);
                    RenderHud((FrameworkElement)hud.FindName("HudRoot"), $"hud-appearance-{theme}-{layout}-{transparency}", transparency == 100, root);
                }
                main.SetDisplayMode("studio"); ThemeManager.SetMode("light"); main.UpdateLayout();
                ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), "hud-global-studio-light");
                main.SetDisplayMode("console"); ThemeManager.SetMode("dark"); main.UpdateLayout();
                ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), "hud-global-console-dark");
                Click("HudNoBackgroundButton"); ((RadioButton)main.FindName("HudLeftOption")).IsChecked = true;
                Click("SaveHudAppearanceButton"); SettingsService.Load();
                Assert.Equal(new HudAppearance("strip", 100, "top-left"), SettingsService.Current.HudAppearance);
                Assert.Equal(new HudAppearance("compact", 75, "top-right"), new HudAppearance("invalid", double.NaN, "invalid").Normalize());
                hud.RecoveryAvailableOverride = () => false; Assert.False(hud.SetClickThrough(true));
                Assert.Equal(Visibility.Visible, ((TextBlock)hud.FindName("ScopeText")).Visibility);
                hud.RecoveryAvailableOverride = () => true; Assert.True(hud.SetClickThrough(true));
                Assert.False(((Button)hud.FindName("CloseHudButton")).IsVisible);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)hud.FindName("LockedHint")).Visibility);
                Click("RestoreHudButton"); Assert.True(((Button)hud.FindName("CloseHudButton")).IsVisible);
                var handle = new WindowInteropHelper(hud).Handle;
                Assert.Equal((nint)3, SendMessage(handle, 0x21, handle, (0x201 << 16) | 1));
                var close = (Button)hud.FindName("CloseHudButton"); hud.UpdateLayout();
                var point = close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), hud);
                point = PresentationSource.FromVisual(hud)!.CompositionTarget.TransformToDevice.Transform(point);
                nint coordinates = ((int)point.Y << 16) | ((int)point.X & 0xffff);
                Assert.True(session.Start("HUD close fixture")); var run = session.RunId;
                SendMessage(handle, 0x200, 0, coordinates);
                SendMessage(handle, 0x201, 1, coordinates);
                SendMessage(handle, 0x202, 0, coordinates);
                DlssFeedbackTests.PumpUntil(() => controller.Window is null);
                Assert.False(metrics.HasSubscribers);
                Assert.True(session.IsRunning); Assert.Equal(run, session.RunId); session.Stop(false);
                Click("RestoreHudButton"); Assert.NotNull(controller.Window);
                Assert.Equal(0, ((FrameworkElement)controller.Window!.FindName("HudBackdrop")).Opacity);
                Click("HideHudButton"); Assert.Null(controller.Window);
            }
            finally { controller.Close(); main.Close(); }
        });
    }
    private static void RenderHud(FrameworkElement element, string name, bool transparent, string testRoot)
    {
        var frame = new DispatcherFrame();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        if (transparent)
        {
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight - 3, 1, 1), pixel, 4, 0);
            Assert.Equal(0, pixel[3]);
        }
        var path = Path.GetFullPath(Path.Combine(testRoot, "../..", name + ".png"));
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
