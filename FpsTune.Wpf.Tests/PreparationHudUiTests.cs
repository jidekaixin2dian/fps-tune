using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using FpsTune.Wpf.Views.Controls;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class PreparationHudUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "preparation-hud036-20261030");
    [Fact]
    public void Preparation_scope_saves_full_mode_and_home_buttons_run_stop_and_keep_related_routes_reachable()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationHudUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var owner = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = owner; DlssFeedbackTests.PrepareBackgroundWindow(owner); owner.Show();
            try
            {
                var dialog = new GamePreparationWindow { Owner = owner }; DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    if (!((Button)dialog.FindName("SaveButton")).IsEnabled) return;
                    timer.Stop(); ((ComboBox)dialog.FindName("MemoryMode")).SelectedIndex = 2;
                    foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(dialog, "preparation-full-" + theme); }
                    Click(dialog, "SaveButton");
                };
                timer.Start(); try { Assert.True(dialog.ShowDialog()); } finally { timer.Stop(); }
                Assert.True(PreparationPlanStore.Resolve(AppState.GamePath).Options.FullMemoryCleanup);
                var memory = new MemoryCleanupTests.FakeMemory(); var apps = new PreparationMemoryIntegrationTests.Apps();
                var page = new ConsoleView(new(new(memory, () => false, () => true), new(apps)));
                var host = new Window { Width = 1000, Height = 720, Content = page }; DlssFeedbackTests.PrepareBackgroundWindow(host); host.Show();
                try
                {
                    Assert.Contains(Str.T("Str.MemoryFullAction"), ((TextBlock)page.FindName("PreparationPlanText")).Text);
                    Click(page, "StartPreparationButton"); DlssFeedbackTests.PumpUntil(() => !GamePreparationService.IsRunning && memory.Purges == 1);
                    DlssFeedbackTests.PumpUntil(() => ((Button)page.FindName("StartPreparationButton")).IsEnabled);
                    Assert.Equal(2, apps.Trimmed); Assert.True(memory.AllPriorities);
                    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                    apps.OnTrim = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(4))); };
                    Click(page, "StartPreparationButton"); DlssFeedbackTests.PumpUntil(() => entered.IsSet);
                    Assert.True(((Button)page.FindName("CancelPreparationButton")).IsVisible); Click(page, "CancelPreparationButton"); release.Set();
                    DlssFeedbackTests.PumpUntil(() => ((Button)page.FindName("StartPreparationButton")).IsEnabled);
                    Assert.Equal(1, memory.Purges); Assert.Contains("已停止", ((TextBlock)page.FindName("PreparationStatus")).Text);
                }
                finally { host.Close(); }
                owner.NavigateTo("games"); owner.NavigateTo("home"); owner.UpdateLayout();
                foreach (var mode in new[] { "studio", "console" }) foreach (var theme in new[] { "light", "dark" })
                {
                    SettingsService.Current.ThemeMode = theme; owner.SetDisplayMode(mode); Render(owner, $"home-{mode}-{theme}");
                }
                var home = (ConsoleView)((ContentControl)owner.FindName("PageHost")).Content;
                Click(home, "GameLibraryButton"); Assert.IsType<GameLibraryView>(((ContentControl)owner.FindName("PageHost")).Content);
                owner.NavigateTo("home"); Click(home, "RecordMatchButton"); Assert.IsType<SessionView>(((ContentControl)owner.FindName("PageHost")).Content);
            }
            finally { owner.Close(); }
        });
    }
    [Fact]
    public void Compact_hud_has_small_bounds_passes_clicks_releases_hidden_sampling_and_keeps_native_close()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationHudUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            using var session = new PerformanceSessionService();
            using var metrics = new MetricsSampler(TimeSpan.FromSeconds(1), 2, () => new(DateTime.Now, 42, 55, 64, 2UL << 30, 8UL << 30));
            var controller = new PerformanceHudService(session, metrics, DlssFeedbackTests.PrepareBackgroundWindow);
            var main = new MainWindow { HudController = controller }; app.MainWindow = main; DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show();
            try
            {
                Click(main, "HudEntryButton"); var hud = controller.Window!;
                hud.RecoveryAvailableOverride = () => true;
                hud.ApplyAppearance(new("compact", 100, "top-right")); metrics.SampleOnce(); hud.UpdateLayout();
                Assert.Equal(420, hud.Width); Assert.InRange(hud.ActualHeight, 35, 90);
                var interactiveHeight = hud.ActualHeight; Assert.True(metrics.HasSubscribers);
                hud.Hide(); Assert.False(metrics.HasSubscribers); hud.Show(); Assert.True(metrics.HasSubscribers);
                var dimensions = new List<object>();
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); hud.SetClickThrough(false); Render(hud, "hud-compact-controls-" + theme);
                    Assert.True(hud.SetClickThrough(true)); hud.UpdateLayout(); Render(hud, "hud-compact-game-" + theme);
                    Assert.False(((FrameworkElement)hud.FindName("InteractionBar")).IsVisible);
                    Assert.True(hud.ActualHeight < interactiveHeight); Assert.InRange(hud.ActualHeight, 25, 48);
                    dimensions.Add(new { theme, hud.Width, Height = hud.ActualHeight, interactiveHeight });
                }
                Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "hud-dimensions.json"), System.Text.Json.JsonSerializer.Serialize(dimensions));
                Click(main, "HudGamePresetButton"); Assert.True(hud.IsClickThrough); Assert.Equal("compact", controller.Appearance.Layout);
                Assert.Equal(100, controller.Appearance.Transparency); Assert.Equal("top-right", controller.Appearance.Position);
                Assert.True(hud.RestoreForInteraction()); hud.UpdateLayout(); Assert.True(((Button)hud.FindName("CloseHudButton")).IsVisible);
                var close = (Button)hud.FindName("CloseHudButton"); var point = close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), hud);
                point = PresentationSource.FromVisual(hud)!.CompositionTarget.TransformToDevice.Transform(point);
                var coordinates = (nint)(((int)point.Y << 16) | ((int)point.X & 0xffff)); var hwnd = new WindowInteropHelper(hud).Handle;
                SendMessage(hwnd, 0x201, 1, coordinates); SendMessage(hwnd, 0x202, 0, coordinates);
                DlssFeedbackTests.PumpUntil(() => controller.Window is null); Assert.False(metrics.HasSubscribers);
            }
            finally { controller.Close(); main.Close(); }
        });
    }
    [Fact]
    public void Preview_releases_raster_sources_on_hide_and_reload_preserves_custom_scene_profile_split_and_original_file()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationHudUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var preview = new ColorPreviewControl(); var window = new Window { Width = 1000, Height = 700, Content = preview };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var before = (Image)preview.FindName("BeforeImage"); var after = (Image)preview.FindName("AfterImage");
                DlssFeedbackTests.PumpUntil(() => before.Source is not null);
                var path = Path.Combine(root, "custom.png"); using (var file = File.Create(path))
                { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ScreenColorPreview.Sample())); encoder.Save(file); }
                var original = File.ReadAllBytes(path); var load = preview.LoadImageAsync(path); DlssFeedbackTests.PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
                var profile = Path.Combine(root, "preview.icm"); File.WriteAllBytes(profile, IccProfileGenerator.Build(IccProfileGenerator.ParamsFor(IccFilterPreset.Warm)));
                var revision = preview.RenderedRevision;
                var convert = preview.SetIccProfileAsync(profile); DlssFeedbackTests.PumpUntil(() => convert.IsCompleted); convert.GetAwaiter().GetResult();
                // 默认是「游戏内效果」：ICC 不参与合成，所以 after 不是 ColorConvertedBitmap。
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > revision);
                Assert.IsAssignableFrom<BitmapSource>(after.Source);
                Assert.Equal(PreviewChannelKind.Game, preview.Channel);
                Assert.Equal(Str.T("Str.ColorPreviewGameHint"), ((TextBlock)preview.FindName("PreviewStatus")).Text);
                revision = preview.RenderedRevision;
                ((RadioButton)preview.FindName("ChannelManaged")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > revision);
                Assert.Equal(PreviewChannelKind.ColorManaged, preview.Channel);
                Assert.Equal(Str.T("Str.ColorPreviewManagedHint"), ((TextBlock)preview.FindName("PreviewStatus")).Text);
                ((Slider)preview.FindName("SplitSlider")).Value = 27;
                var samples = new List<object>();
                for (var i = 0; i < 5; i++)
                {
                    var raster = Assert.IsAssignableFrom<BitmapSource>(before.Source);
                    var pixels = raster.PixelWidth * (long)raster.PixelHeight;
                    var render = preview.RenderedRevision;
                    preview.Visibility = Visibility.Collapsed; Assert.Null(before.Source); Assert.Null(after.Source);
                    samples.Add(new { round = i, activePixels = pixels, hiddenReferencedPixels = 0, channel = preview.Channel.ToString() });
                    preview.Visibility = Visibility.Visible;
                    DlssFeedbackTests.PumpUntil(() => after.Source is BitmapSource && preview.RenderedRevision > render);
                    Assert.Equal(PreviewChannelKind.ColorManaged, preview.Channel);
                    Assert.Equal(2, ((ComboBox)preview.FindName("ScreenshotCombo")).SelectedIndex); Assert.Equal(27, ((Slider)preview.FindName("SplitSlider")).Value);
                    Assert.Equal("custom.png", ((FileNameLabel)preview.FindName("ImageNameText")).Text);
                }
                ((ComboBox)preview.FindName("ScreenshotCombo")).SelectedIndex = 0;
                preview.Visibility = Visibility.Collapsed;
                var drain = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                timer.Tick += (_, _) => { timer.Stop(); drain.Continue = false; }; timer.Start(); Dispatcher.PushFrame(drain);
                Assert.Null(before.Source); Assert.Null(after.Source); Assert.Equal(original, File.ReadAllBytes(path));
                Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "preview-retention.json"), System.Text.Json.JsonSerializer.Serialize(samples));
            }
            finally { window.Close(); }
        });
    }
    private static void Click(FrameworkElement element, string name) => ((Button)element.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Render(Window window, string name)
    {
        var root = (FrameworkElement)window.Content; window.UpdateLayout(); var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        var dpi = VisualTreeHelper.GetDpi(root); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen())
        {
            if (window is not PerformanceHudWindow) dc.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(drawing); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        if (window is PerformanceHudWindow hud && !((FrameworkElement)hud.FindName("InteractionBar")).IsVisible)
            Assert.Equal(0, pixels[^1]); // Clear game strip preserves actual alpha outside its text.
        Assert.True(pixels.Chunk(4).Select(p => BitConverter.ToInt32(p)).Distinct().Take(20).Count() > 10);
        Directory.CreateDirectory(Path.Combine(Output, "ui")); using var stream = File.Create(Path.Combine(Output, "ui", name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
