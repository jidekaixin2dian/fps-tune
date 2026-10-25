using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ResolutionUiTests
{
    [Fact]
    public void Resolution_page_renders_in_both_themes_and_modes_and_runs_edit_confirm_restore_and_failure_interactions()
    {
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        { DlssFeedbackTests.RunIsolated(testType: typeof(ResolutionUiTests)); return; }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            using var f = new ResolutionTests.Fixture();
            var oldGame = AppState.GamePath;
            try
            {
                DisplayQualityService.ApiOverride = () => new DisplayQualityTests.FakeNvdrsApi();
                AppState.GamePath = null; SettingsService.Current.HotkeyEnabled = false;
                var compiled = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; compiled.InitializeComponent(); app = compiled;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                LangService.Apply(LangService.ZhCn); ThemeManager.Initialize();
                var window = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = window;
                DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
                var report = new List<object>();
                foreach (var mode in new[] { "studio", "console" })
                foreach (var theme in new[] { "dark", "light" })
                {
                    window.SetDisplayMode(mode); ThemeManager.SetMode(theme); window.NavigateTo("display"); window.UpdateLayout();
                    var page = (DisplayQualityView)((ContentControl)window.FindName("PageHost")).Content;
                    ((RadioButton)page.FindName("SubTabResolution")).IsChecked = true;
                    var panel = (ResolutionPanel)page.FindName("ResolutionContent");
                    var refresh = panel.RefreshAsync(); DlssFeedbackTests.PumpUntil(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
                    var presets = (ItemsControl)panel.FindName("Presets");
                    DlssFeedbackTests.PumpUntil(() => presets.Items.Count == 4 && ((Button)panel.FindName("ApplyButton")).IsEnabled);
                    window.UpdateLayout();
                    var radios = Descendants(presets).OfType<RadioButton>().ToArray(); Assert.Equal(4, radios.Length);
                    radios[2].IsChecked = true;
                    Assert.Equal("1440", ((TextBox)panel.FindName("WidthInput")).Text);
                    Assert.Equal("1080", ((TextBox)panel.FindName("HeightInput")).Text);
                    Assert.Equal(240, ((ComboBox)panel.FindName("RefreshRate")).SelectedItem);
                    ((TextBox)panel.FindName("HeightInput")).Text = "960";
                    Assert.Equal("1280", ((TextBox)panel.FindName("WidthInput")).Text);
                    ((CheckBox)panel.FindName("LockRatio")).IsChecked = false;
                    ((TextBox)panel.FindName("WidthInput")).Text = "1440";
                    Assert.Equal("960", ((TextBox)panel.FindName("HeightInput")).Text);
                    ((CheckBox)panel.FindName("LockRatio")).IsChecked = true;
                    Assert.Equal("1080", ((TextBox)panel.FindName("HeightInput")).Text);
                    DlssFeedbackTests.PumpUntil(() => ((Button)panel.FindName("ApplyButton")).IsEnabled);
                    Assert.Contains("NVIDIA", ((TextBlock)panel.FindName("PlanText")).Text);
                    ((FrameworkElement)panel.FindName("Target")).BringIntoView(); window.UpdateLayout();
                    Capture((FrameworkElement)window.FindName("ChromeRoot"), mode + "-" + theme, report);
                }
                var view = (DisplayQualityView)((ContentControl)window.FindName("PageHost")).Content;
                var control = (ResolutionPanel)view.FindName("ResolutionContent");
                ((ComboBox)control.FindName("Scaling")).SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => ((Button)control.FindName("ApplyButton")).IsEnabled);
                var apply = (Button)control.FindName("ApplyButton");
                var width = (TextBox)control.FindName("WidthInput");
                width.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, width, apply)
                    { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                Assert.True(apply.IsEnabled); // Leaving an already-normalized field must not discard the ready plan.
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(((StackPanel)control.FindName("Inputs")).IsEnabled);
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // Busy calls must be ignored.
                DlssFeedbackTests.PumpUntil(() => ((Border)control.FindName("Confirmation")).Visibility == Visibility.Visible
                    && ((Button)control.FindName("KeepButton")).IsEnabled);
                Assert.Equal(1, f.Trials); Assert.Equal(0, f.Saves);
                ((FrameworkElement)control.FindName("Confirmation")).BringIntoView(); window.UpdateLayout();
                Capture((FrameworkElement)window.FindName("ChromeRoot"), "confirmation", report);
                ((Button)control.FindName("KeepButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => f.Saves == 1 && ((Button)control.FindName("RestoreButton")).IsEnabled);
                ((Button)control.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => f.Current == ResolutionTests.Fixture.Native && f.Scale == 5);
                DlssFeedbackTests.PumpUntil(() => ((Button)control.FindName("ApplyButton")).IsEnabled);
                ((ComboBox)control.FindName("Target")).SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)control.FindName("CurrentText")).Text.Contains("2560"));
                ((ComboBox)control.FindName("Target")).SelectedIndex = 0;
                DlssFeedbackTests.PumpUntil(() => ((Button)control.FindName("ApplyButton")).IsEnabled);
                f.OtherCurrent = new(1920, 1080, 144);
                ((ComboBox)control.FindName("Target")).SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)control.FindName("CurrentText")).Text.Contains("1920"));
                Assert.Equal(144, ((ComboBox)control.FindName("RefreshRate")).SelectedItem);
                ((ComboBox)control.FindName("Target")).SelectedIndex = 0;
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)control.FindName("CurrentText")).Text.Contains("2560"));
                Assert.Equal(240, ((ComboBox)control.FindName("RefreshRate")).SelectedItem);
                f.IgnoreScaling = true;
                ((ComboBox)control.FindName("Scaling")).SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => ((Button)control.FindName("ApplyButton")).IsEnabled);
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)control.FindName("ScalingText")).Text.Contains(Str.T("Str.Resolution.ResolutionService.29")));
                Assert.Equal(ResolutionTests.Fixture.Native, f.Current);
                f.IgnoreScaling = false;
                f.RejectCustom = true;
                ((TextBox)control.FindName("WidthInput")).Text = "1664";
                ((TextBox)control.FindName("HeightInput")).Text = "1248";
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)control.FindName("PlanText")).Text.Contains("custom timing unavailable"));
                Assert.False(((Button)control.FindName("ApplyButton")).IsEnabled);
                f.RejectCustom = false;
                var guard = ResolutionService.GuardOverride;
                var token = f.Begin(1024, 768);
                ResolutionService.GuardOverride = null; f.Connected = false;
                var reopened = new DisplayQualityView();
                ((ContentControl)window.FindName("PageHost")).Content = reopened;
                ((RadioButton)reopened.FindName("SubTabResolution")).IsChecked = true;
                var recovery = (ResolutionPanel)reopened.FindName("ResolutionContent");
                var reload = recovery.RefreshAsync(); DlssFeedbackTests.PumpUntil(() => reload.IsCompleted); reload.GetAwaiter().GetResult();
                DlssFeedbackTests.PumpUntil(() => ((Border)recovery.FindName("Confirmation")).Visibility == Visibility.Visible
                    && ((TextBlock)recovery.FindName("CountdownText")).Text.Contains("回退尚未完成"));
                Assert.False(((Button)recovery.FindName("KeepButton")).IsEnabled);
                Assert.True(((Button)recovery.FindName("RevertButton")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((Border)recovery.FindName("Confirmation")).Visibility);
                Assert.Contains("回退尚未完成", ((TextBlock)recovery.FindName("CountdownText")).Text);
                Assert.Contains("未连接", ((ComboBox)recovery.FindName("Target")).SelectedItem!.ToString());
                Assert.Contains("原显示器", ((TextBlock)recovery.FindName("CurrentText")).Text);
                ((FrameworkElement)recovery.FindName("Confirmation")).BringIntoView();
                Capture((FrameworkElement)window.FindName("ChromeRoot"), "stopped-disconnected-recovery", report);
                f.Connected = true;
                ((Button)recovery.FindName("RevertButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => f.Current == ResolutionTests.Fixture.Native && ((Border)recovery.FindName("Confirmation")).Visibility == Visibility.Collapsed);
                ResolutionService.GuardOverride = guard;
                token = f.Begin(1024, 768);
                reload = recovery.RefreshAsync(); DlssFeedbackTests.PumpUntil(() => reload.IsCompleted); reload.GetAwaiter().GetResult();
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(key); Assert.True(key.Handled);
                DlssFeedbackTests.PumpUntil(() => f.Current == ResolutionTests.Fixture.Native && ((Border)recovery.FindName("Confirmation")).Visibility == Visibility.Collapsed);
                token = f.Begin(1024, 768);
                reload = recovery.RefreshAsync(); DlssFeedbackTests.PumpUntil(() => reload.IsCompleted); reload.GetAwaiter().GetResult();
                File.Delete(f.Receipt);
                var poll = recovery.PollTrialAsync(); DlssFeedbackTests.PumpUntil(() => poll.IsCompleted); poll.GetAwaiter().GetResult();
                Assert.Contains("无法确认是否已回退", ((TextBlock)recovery.FindName("ResultText")).Text);
                Assert.Equal(1024, f.Current.Width);
                var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "resolution-recovery-20261024", "ui"); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "report.json"), System.Text.Json.JsonSerializer.Serialize(report));
                window.Close();
            }
            catch (Exception ex) { error = ex; }
            finally { app?.Shutdown(); DisplayQualityService.ApiOverride = null; AppState.GamePath = oldGame; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Resolution UI check did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Capture(FrameworkElement element, string name, List<object> report)
    {
        var frame = new DispatcherFrame();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var dir = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "resolution-recovery-20261024", "ui"); Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, name + ".png")); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        report.Add(new { name, element.ActualWidth, element.ActualHeight, backend = "isolated display API; real WPF rendering" });
    }
}
