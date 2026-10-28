using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class HudAutomationUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static void Wait(Task task) { DlssFeedbackTests.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static PerformanceSessionService HardwareOnly() => new((interval, capacity) => new MetricsSampler(interval, capacity,
        () => new(DateTime.Now, 30, 50, 70, 2UL << 30, 8UL << 30)), () => new SessionFpsRecorder(findPresentMon: () => null));

    [Fact]
    public void Automatic_recording_respects_baselines_identity_uncertainty_manual_ownership_and_save_on_exit()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(HudAutomationUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            using var session = HardwareOnly(); var enabled = false; var probes = 0;
            var path = Path.Combine(root, "Game.exe"); var originalGame = AppState.GamePath;
            GameProcessObservation observation = new(new(10, 1)); var busy = false;
            using var automation = new AutoRecordingService(session, () => (enabled, new[] { path }),
                _ => { probes++; return observation; }, () => busy);
            void Poll() => Wait(automation.PollAsync());
            Poll(); Assert.Equal(0, probes); Assert.False(session.IsRunning);
            enabled = true; Poll(); Assert.False(session.IsRunning); // Enabling on an already running game establishes a baseline.
            observation = new(null); Poll(); observation = new(new(10, 1)); Poll();
            Assert.True(session.IsRunning); Assert.Equal(originalGame, AppState.GamePath);
            Assert.Equal(SessionConditionRules.CaptureGame(path, null).GameKey, session.Conditions!.GameKey);
            var owned = session.RunId;
            observation = new(null, false); Poll(); Assert.True(session.IsRunning); Assert.Equal(owned, session.RunId);
            observation = new(null); var before = PerformanceSessionStore.LoadHistory().Count; Poll();
            Assert.False(session.IsRunning); Assert.Equal(before + 1, PerformanceSessionStore.LoadHistory().Count);
            Assert.True(session.Start("Manual")); var manual = session.RunId;
            observation = new(new(10, 2)); Poll(); observation = new(null); Poll();
            Assert.True(session.IsRunning); Assert.Equal(manual, session.RunId); session.Stop(false);
            observation = new(new(10, 3)); Poll(); Assert.True(session.IsRunning);
            enabled = false; Poll(); Assert.False(session.IsRunning); Assert.False(automation.IsObserving);
            enabled = true; observation = new(null); Poll();
            busy = true; observation = new(new(10, 4)); Poll(); Assert.False(session.IsRunning);
            busy = false; Poll(); Assert.False(session.IsRunning); // Busy launch is consumed, not retried mid-match.
            observation = new(null); Poll(); observation = new(new(10, 5)); Poll(); owned = session.RunId;
            observation = new(new(10, 6)); Poll(); Assert.True(session.IsRunning); Assert.NotEqual(owned, session.RunId);
            session.Stop(false); Assert.True(session.Start("Replacement manual")); manual = session.RunId;
            observation = new(null); Poll(); Assert.Equal(manual, session.RunId); Assert.True(session.IsRunning); session.Stop(false);
            using var self = Process.GetCurrentProcess(); var image = ProcessIdentity.TryGetImagePath(self)!;
            Assert.NotNull(AutoRecordingService.ObserveProcess(image).Run);
            Assert.Null(AutoRecordingService.ObserveProcess(Path.Combine(root, "different", Path.GetFileName(image))).Run);
        });
    }

    [Fact]
    public void Hud_and_session_controls_render_handle_live_data_unlock_and_release_without_duplicate_sampling()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(HudAutomationUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            SessionFpsRecorder? recorder = null;
            var rows = new[] { "FrameTime" }.Concat(Enumerable.Repeat("8", 60)).ToArray();
            using var session = new PerformanceSessionService((interval, capacity) => new MetricsSampler(interval, capacity,
                () => new(DateTime.Now, 30, 50, 70, 2UL << 30, 8UL << 30)), () => recorder = new SessionFpsRecorder(
                () => "fixture-presentmon.exe", _ => Process.GetCurrentProcess(), _ => new Process(), _ => Task.FromResult(rows), _ => { }));
            using var metrics = new MetricsSampler(TimeSpan.FromSeconds(1), 60, () => new(DateTime.Now, 42, 55, 64, 2UL << 30, 8UL << 30));
            using var automation = new AutoRecordingService(session);
            var controller = new PerformanceHudService(session, metrics, DlssFeedbackTests.PrepareBackgroundWindow);
            var page = new SessionView(session, automation);
            controller.PreviewAppearance(new("card", 0, "top-right"));
            var main = new MainWindow { Width = 1180, Height = 720, HudController = controller }; app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show(); main.NavigateTo("session");
            ((ContentControl)main.FindName("PageHost")).Content = page;
            try
            {
                DlssFeedbackTests.PumpUntil(() => page.IsLoaded && ((ItemsControl)page.FindName("AutoRecordGames")).Items.Count > 0);
                ((Expander)page.FindName("GettingStarted")).IsExpanded = false;
                ((Expander)page.FindName("AutoRecordOptions")).IsExpanded = true;
                main.UpdateLayout();
                Assert.False(((CheckBox)page.FindName("AutoRecordEnabled")).IsChecked);
                ((CheckBox)page.FindName("AutoRecordEnabled")).IsChecked = true;
                ((Button)page.FindName("AutoRecordSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Str.T("Str.AutoRecordSelectGames"), ((TextBlock)page.FindName("AutoRecordStatus")).Text);
                var games = (ItemsControl)page.FindName("AutoRecordGames");
                main.UpdateLayout();
                ReadabilityAndFramesUiTests.Descendants(games).OfType<CheckBox>().First().IsChecked = true;
                ((Button)page.FindName("AutoRecordSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(SettingsService.Current.AutoRecordEnabled);
                Assert.Contains(AppState.GamePath!, SettingsService.Current.AutoRecordGames);
                ((CheckBox)page.FindName("AutoRecordEnabled")).IsChecked = false;
                ((Button)page.FindName("AutoRecordSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)main.FindName("RestoreHudButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(controller.Window);
                var hud = controller.Window!; DlssFeedbackTests.AssertBackgroundWindow(hud);
                Assert.True(metrics.HasSubscribers); Assert.False(metrics.IsRunning);
                controller.Show(); Assert.Same(hud, controller.Window);
                metrics.SampleOnce();
                Assert.Equal("CPU  42%", ((TextBlock)hud.FindName("CpuText")).Text);
                Assert.Equal("—", ((TextBlock)hud.FindName("FpsText")).Text);
                Assert.True(session.Start("HUD fixture"));
                void Feed()
                {
                    using var output = new MemoryStream();
                    Wait(SessionFpsRecorder.CaptureOutputAsync(new StringReader(string.Join('\n', rows)), output, 4096, 100,
                        () => Assert.Fail("Unexpected limit"), recorder!.ObserveFrame));
                    hud.Refresh();
                }
                Feed(); Assert.Equal("125", ((TextBlock)hud.FindName("FpsText")).Text);
                foreach (var mode in new[] { "studio", "console" })
                foreach (var theme in new[] { "light", "dark" })
                {
                    main.SetDisplayMode(mode); ThemeManager.SetMode(theme); main.UpdateLayout(); hud.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)hud.FindName("HudRoot"), "hud-" + mode + "-" + theme);
                    ((FrameworkElement)main.FindName("HudEntryButton")).BringIntoView(); main.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), "session-hud-entry-" + mode + "-" + theme);
                    ((FrameworkElement)page.FindName("AutoRecordOptions")).BringIntoView(); main.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), "session-automation-" + mode + "-" + theme);
                }
                ((CheckBox)hud.FindName("Pin")).IsChecked = false; Assert.False(hud.Topmost);
                hud.RecoveryAvailableOverride = () => false; Assert.False(hud.SetClickThrough(true));
                hud.Refresh(); Assert.Equal(Str.T("Str.HudRecoveryUnavailable"), ((TextBlock)hud.FindName("ScopeText")).Text);
                hud.RecoveryAvailableOverride = () => true; Assert.True(hud.SetClickThrough(true));
                var handle = new System.Windows.Interop.WindowInteropHelper(hud).Handle;
                Assert.NotEqual(0, GetWindowLongPtr(handle, -20) & 0x20);
                hud.RecoveryWorkAreaOverride = (-24000, -24000, -22000, -22000);
                controller.Restore(); Assert.False(hud.IsClickThrough); Assert.Equal(0, GetWindowLongPtr(handle, -20) & 0x20);
                Assert.False(hud.Topmost); DlssFeedbackTests.AssertBackgroundWindow(hud);
                Assert.True(GetWindowRect(handle, out var restored));
                Assert.InRange(restored.Left, -24000, -22001); Assert.Equal(-22016, restored.Right); Assert.Equal(-23984, restored.Top);
                hud.Hide(); Assert.Null(session.CurrentLiveFps);
                ((Button)main.FindName("RestoreHudButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Feed();
                Assert.True(hud.IsVisible); Assert.False(hud.IsActive);
                Assert.Equal("125", ((TextBlock)hud.FindName("FpsText")).Text);
                hud.WindowState = WindowState.Minimized;
                ((Button)main.FindName("RestoreHudButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WindowState.Normal, hud.WindowState); Assert.True(hud.IsVisible);
                var run = session.RunId; controller.Close();
                Assert.True(session.IsRunning); Assert.Equal(run, session.RunId);
                Assert.False(metrics.HasSubscribers); Assert.Null(session.CurrentLiveFps);
                ((Button)main.FindName("RestoreHudButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                hud = controller.Window!; Feed();
                Assert.Equal("125", ((TextBlock)hud.FindName("FpsText")).Text);
                Wait(session.StopAsync(false)); Assert.Equal("—", ((TextBlock)hud.FindName("FpsText")).Text);
                ((Button)hud.FindName("CloseHudButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(controller.Window); Assert.False(metrics.HasSubscribers);
            }
            finally { controller.Close(); main.Close(); }
        });
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
}
