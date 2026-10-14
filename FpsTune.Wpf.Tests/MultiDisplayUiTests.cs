using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class MultiDisplayUiTests
{
    [Fact]
    public void Actual_selected_screen_buttons_keep_target_on_primary_change_and_disable_unplugged_screen_without_fallback()
    {
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        { DlssFeedbackTests.RunIsolated(testType: typeof(MultiDisplayUiTests)); return; }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            using var fixture = new MultiDisplayColorTests();
            Application? application = null;
            Window? window = null;
            DispatcherTimer? confirm = null;
            try
            {
                UserDataPaths.RootOverride = fixture.Root;
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = DlssFeedbackTests.LoadResources() };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
                var page = new DisplayQualityView();
                window = new Window { Width = 1050, Height = 850, Content = page };
                application.MainWindow = window;
                window.Show();
                var combo = (ComboBox)page.FindName("DisplayTargetCombo");
                var vib = (Button)page.FindName("VibApplyButton");
                var icc = (Button)page.FindName("IccApplyButton");
                DlssFeedbackTests.PumpUntil(() => combo.Items.Count == 2 && vib.IsEnabled && icc.IsEnabled);
                Assert.True(((Button)page.FindName("IccApplyFileButton")).IsEnabled);
                var subtab = FindVisual<RadioButton>(page).Single(b => Equals(b.Tag, "vibicc"));
                subtab.IsChecked = true;
                combo.SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => vib.IsEnabled && ((Slider)page.FindName("VibSlider")).Value == 60);
                Assert.DoesNotContain("Identity", combo.Text);
                confirm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                confirm.Tick += (_, _) =>
                {
                    var dialog = application.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible);
                    if (dialog is null) return;
                    ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                confirm.Start();
                ((Slider)page.FindName("VibSlider")).Value = 85;
                vib.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(combo.IsEnabled);
                DlssFeedbackTests.PumpUntil(() => vib.IsEnabled && ((TextBlock)page.FindName("VibStateText")).Text.Contains("85"));
                Assert.Equal(85, fixture.Levels["B"]);
                Assert.Equal(30, fixture.Levels["A"]);
                fixture.Displays[0] = fixture.Displays[0] with { IsPrimary = false };
                fixture.Displays[1] = fixture.Displays[1] with { IsPrimary = true };
                ((Button)page.FindName("DisplayRefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && combo.Text.Contains("Screen B"));
                icc.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && ((TextBlock)page.FindName("IccStateText")).Text.Contains("Vivid"));
                Assert.Equal(["a.icm"], fixture.Profiles["A"]);
                Assert.Equal("FpsTune-Vivid.icc", fixture.Profiles["B"].Last());
                foreach (var lang in new[] { LangService.ZhCn, LangService.EnUs })
                {
                    LangService.Apply(lang);
                    ((Button)page.FindName("DisplayRefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && combo.Text.Contains("Screen B"));
                    Render(page, "selected-B-" + lang + ".png");
                }
                var b = fixture.Displays.Single(d => d.Identity == "B");
                fixture.Displays.Remove(b);
                ((Button)page.FindName("DisplayRefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => combo.IsEnabled && combo.SelectedItem is null);
                Assert.False(vib.IsEnabled);
                Assert.False(icc.IsEnabled);
                Assert.Contains(Str.T("Str.DisplayDisconnected"), ((TextBlock)page.FindName("DisplayTargetState")).Text);
                fixture.Displays.Add(b);
                ((Button)page.FindName("DisplayRefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && combo.Text.Contains("Screen B"));
                ((Button)page.FindName("VibRestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => vib.IsEnabled && ((Slider)page.FindName("VibSlider")).Value == 60);
                ((Button)page.FindName("IccRestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && ((TextBlock)page.FindName("IccStateText")).Text == Str.T("Str.IccRestored"));
                Assert.Equal(["b.icm"], fixture.Profiles["B"]);
                Assert.Empty(IccFilterService.BackupTargets());
                Assert.Empty(DigitalVibranceService.BackupTargets());
            }
            catch (Exception ex) { error = ex; }
            finally { confirm?.Stop(); window?.Close(); application?.Shutdown(); UserDataPaths.RootOverride = null; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Multi-display WPF test did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisual<T>(child)) yield return descendant;
        }
    }
    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "multi-display", "ui");
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
