using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ReadabilityAndFramesUiTests
{
    [Fact]
    public void Navigation_weight_stays_constant_and_all_pages_render_with_readable_guides_and_distinct_series()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var window = new MainWindow { Width = 1180, Height = 800 };
            window.NavigateTo("opt"); app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var nav = (RadioButton)window.FindName("NavAb"); nav.ApplyTemplate();
                var label = Descendants(nav).OfType<TextBlock>().Single(t => t.Text == Str.T("Str.NavAb"));
                var weight = label.FontWeight; var size = label.FontSize;
                nav.IsChecked = true; window.UpdateLayout();
                Assert.Equal(weight, label.FontWeight); Assert.Equal(size, label.FontSize);
                var report = new List<object>();
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme);
                    Assert.NotEqual(((SolidColorBrush)app.FindResource("AccentBrush")).Color,
                        ((SolidColorBrush)app.FindResource("ChartSecondaryBrush")).Color);
                    foreach (var route in new[] { "home", "detect", "opt", "display", "session", "ab", "backup", "tools", "settings" })
                    {
                        window.NavigateTo(route); window.UpdateLayout();
                        var page = (FrameworkElement)((ContentControl)window.FindName("PageHost")).Content;
                        DlssFeedbackTests.PumpUntil(() => page.IsLoaded);
                        if (page is DisplayQualityView)
                            DlssFeedbackTests.PumpUntil(() => ((Grid)page.FindName("FrameControls")).IsEnabled);
                        if (page is SessionView or AbExperimentView)
                        {
                            var guide = (Expander)page.FindName("GettingStarted");
                            Assert.True(guide.IsExpanded); guide.IsExpanded = false; Assert.False(guide.IsExpanded); guide.IsExpanded = true;
                        }
                        Capture(window, route + "-" + theme, report);
                        if (page is SettingsView)
                            for (var i = 0; i < 6; i++) { ((RadioButton)page.FindName("SettingsNav" + i)).IsChecked = true; Capture(window, "settings" + i + "-" + theme, report); }
                        if (page is MaintenanceView)
                            foreach (var name in new[] { "StorageNav", "ProcessesNav", "TroubleshootNav" })
                            { ((RadioButton)page.FindName(name)).IsChecked = true; Capture(window, "tools-" + name + "-" + theme, report); }
                        if (page is DisplayQualityView)
                            foreach (var tab in Descendants(page).OfType<RadioButton>().Where(b => b.Tag is string s && new[] { "dlss", "vibicc", "drs", "identity", "advice" }.Contains(s)).ToArray())
                            { tab.IsChecked = true; Capture(window, "display-" + tab.Tag + "-" + theme, report); }
                    }
                }
                File.WriteAllText(Path.Combine(Output(), "readability-audit.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Actual_frame_buttons_validate_commit_restore_only_caps_and_keep_failed_edits_visible()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, root) =>
        {
            var exe = Path.GetFileName(AppState.GamePath)!;
            var api = (DisplayQualityTests.FakeNvdrsApi)DisplayQualityService.ApiOverride!();
            var profile = api.AddProfile("existing", exe);
            profile.Settings[DisplayQualityService.FrameRateLimitId] = 90;
            DisplayQualityService.ApplyDlssPreset(exe, DlssPreset.PresetK);
            var page = new DisplayQualityView();
            var window = new Window { Width = 1180, Height = 800, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible);
                if (dialog is not null) ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            try
            {
                Descendants(page).OfType<RadioButton>().Single(b => b.Tag as string == "drs").IsChecked = true;
                var controls = (Grid)page.FindName("FrameControls");
                var apply = (Button)page.FindName("FrameApplyButton"); var restore = (Button)page.FindName("FrameRestoreButton");
                var state = (TextBlock)page.FindName("FrameStateText");
                DlssFeedbackTests.PumpUntil(() => controls.IsEnabled);
                var baselineSaves = api.SaveCount;
                var mainCheck = (CheckBox)page.FindName("MainCapCheck"); var mainBox = (TextBox)page.FindName("MainCapBox");
                mainCheck.IsChecked = true; mainBox.Text = "19";
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(baselineSaves, api.SaveCount); Assert.Equal(Str.T("Str.FrameRange"), state.Text);
                mainBox.Text = "144"; ((CheckBox)page.FindName("BackgroundCapCheck")).IsChecked = true;
                ((TextBox)page.FindName("BackgroundCapBox")).Text = "30";
                timer.Start(); apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(controls.IsEnabled);
                DlssFeedbackTests.PumpUntil(() => state.Text == Str.T("Str.FrameApplied") && controls.IsEnabled);
                Assert.Equal(baselineSaves + 1, api.SaveCount);
                Assert.Equal(144u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
                Assert.Equal(30u, profile.Settings[DisplayQualityService.BackgroundFrameRateLimitId]);
                Assert.True(restore.IsEnabled);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); apply.BringIntoView(); window.UpdateLayout();
                    Render((FrameworkElement)window.Content, "frames-" + theme);
                }
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => state.Text == Str.T("Str.FrameRestored") && controls.IsEnabled);
                Assert.Equal(90u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
                Assert.False(profile.Settings.ContainsKey(DisplayQualityService.BackgroundFrameRateLimitId));
                Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
                api.SimulateSaveIgnored = true;
                mainCheck.IsChecked = true; mainBox.Text = "165";
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => controls.IsEnabled && state.Text.StartsWith(Str.T("Str.FrameFailed", ""), StringComparison.Ordinal));
                Assert.Equal("165", mainBox.Text); Assert.True(mainCheck.IsChecked);
                Assert.Equal(90u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
            }
            finally { timer.Stop(); window.Close(); }
        });
    }

    [Fact]
    public void Memory_button_handles_busy_elevation_pending_success_and_failures_without_real_cleanup()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var busy = false; var admin = true;
            var api = new MemoryCleanupTests.FakeMemory();
            var page = new MaintenanceView(new MemoryCleanupService(api, () => busy, () => admin));
            var window = new Window { Width = 1180, Height = 800, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            var button = (Button)page.FindName("MemoryCleanButton");
            var status = (TextBlock)page.FindName("MemoryResult");
            var refresh = (Button)page.FindName("RefreshButton");
            try
            {
                ((RadioButton)page.FindName("ProcessesNav")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => button.IsEnabled && ((TextBlock)page.FindName("MemoryAvailable")).Text.Contains("5.00"));
                busy = true; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Str.T("Str.MemoryBusy"), status.Text); Assert.Equal(0, api.Purges); busy = false;
                admin = false;
                var cancel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                cancel.Tick += (_, _) =>
                {
                    var dialog = app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible);
                    if (dialog is not null) ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                cancel.Start();
                try { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                finally { cancel.Stop(); }
                Assert.Equal(0, api.Purges); admin = true;
                using var hold = new ManualResetEventSlim(); api.Hold = hold;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => Volatile.Read(ref api.Purges) == 1);
                Assert.False(button.IsEnabled); Assert.False(refresh.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, api.Purges);
                hold.Set(); DlssFeedbackTests.PumpUntil(() => button.IsEnabled);
                Assert.Equal(Str.T("Str.MemoryCleaned", "5.00 GiB", "5.00 GiB"), status.Text);
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(page, "memory-" + theme); }
                api.Hold = null; api.PurgeError = new System.ComponentModel.Win32Exception(1314);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); DlssFeedbackTests.PumpUntil(() => button.IsEnabled);
                Assert.StartsWith(Str.T("Str.MemoryFailed", ""), status.Text); Assert.True(refresh.IsEnabled);
                api.PurgeError = null; api.FailRead = api.Reads + 2;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); DlssFeedbackTests.PumpUntil(() => button.IsEnabled);
                Assert.Contains("snapshot unavailable", status.Text); Assert.Contains("刷新", status.Text);
            }
            finally { window.Close(); }
        });
    }

    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static void RunUi(Action<Application, string> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? app = null; var oldGame = AppState.GamePath; var oldItems = AppState.Items; var oldSpec = UiPerformance.LowSpec;
            try
            {
                var root = Path.Combine(Output(), "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                UserDataPaths.RootOverride = root; UiPerformance.LowSpec = true;
                AppState.GamePath = Path.Combine(root, "FixtureGame.exe"); File.WriteAllText(AppState.GamePath, "fixture; never executed");
                PerformanceSessionStore.Save(SessionConditionTests.Example());
                PerformanceSessionStore.Save(SessionConditionTests.Example("Comparison"));
                DisplayQualityService.ApiOverride = () => Api;
                DisplayQualityService.BackupDirOverride = Path.Combine(root, "display-quality"); DisplayTopology.Override = () => [];
                AppState.Items = ItemCatalog.All.Where(d => d.Available).Select(d => new OptimizationItem(d.Id, d.DisplayName, d.DisplayDescription,
                    d.DisplaySideEffect, d.Admin, d.Reboot, false, "test original", false, d.Group)).ToList();
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = DlssFeedbackTests.LoadResources() };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                LangService.Apply(LangService.ZhCn); DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
                SettingsService.Current.HotkeyEnabled = false;
                test(app, root);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                app?.Shutdown(); DisplayQualityService.ApiOverride = null; DisplayQualityService.BackupDirOverride = null;
                DisplayTopology.Override = null; DialogService.PrepareWindowForTest = null; UserDataPaths.RootOverride = null;
                AppState.GamePath = oldGame; AppState.Items = oldItems; UiPerformance.LowSpec = oldSpec;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Readability / frame UI check did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static readonly DisplayQualityTests.FakeNvdrsApi Api = new();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static string Output()
    {
        var path = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "readability-frames", "ui"); Directory.CreateDirectory(path); return path;
    }
    private static void Capture(MainWindow window, string name, List<object> report)
    {
        window.UpdateLayout();
        var texts = Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).ToArray();
        Assert.DoesNotContain(texts, t => t.FontSize < 12);
        report.Add(new { page = name, count = texts.Length, minFontDip = texts.Select(t => t.FontSize).DefaultIfEmpty(12).Min(),
            below12 = texts.Where(t => t.FontSize < 12).Select(t => new { t.Text, t.FontSize }).ToArray() });
        Render((FrameworkElement)window.FindName("ChromeRoot"), name);
    }
    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(visual);
        using var stream = File.Create(Path.Combine(Output(), name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
