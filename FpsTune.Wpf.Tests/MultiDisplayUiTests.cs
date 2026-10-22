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
            fixture.Profiles["B"].Clear();
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
                DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
                DlssFeedbackTests.PrepareBackgroundWindow(window);
                window.Show();
                var combo = (ComboBox)page.FindName("DisplayTargetCombo");
                var color = (ScreenColorPanel)page.FindName("ColorPanel");
                var applyColor = (Button)color.FindName("ColorApplyButton");
                var vib = (Button)page.FindName("VibApplyButton");
                var icc = (Button)page.FindName("IccApplyButton");
                var subtab = FindVisual<RadioButton>(page).Single(b => Equals(b.Tag, "vibicc"));
                subtab.IsChecked = true;
                ((Expander)page.FindName("IccAdvanced")).IsExpanded = true;
                DlssFeedbackTests.PumpUntil(() => combo.Items.Count == 2 && vib.IsEnabled && icc.IsEnabled);
                Assert.True(((Button)page.FindName("IccApplyFileButton")).IsEnabled);
                combo.SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => vib.IsEnabled && ((Slider)page.FindName("VibSlider")).Value == 60);
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && ((TextBlock)page.FindName("IccCurrentText")).Text.Contains(Str.T("Str.DisplayNoCustomIcc")));
                Assert.True(((Button)page.FindName("IccApplyFileButton")).IsEnabled);
                Assert.False(((Button)page.FindName("IccRestoreButton")).IsEnabled);
                Render(page, "windows-default-B.png");
                Assert.DoesNotContain("Identity", combo.Text);
                confirm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                confirm.Tick += (_, _) =>
                {
                    var dialog = application.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible);
                    if (dialog is null) return;
                    DlssFeedbackTests.AssertBackgroundWindow(dialog);
                    ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                confirm.Start();
                DlssFeedbackTests.PumpUntil(() => applyColor.IsEnabled);
                ((Slider)page.FindName("VibSlider")).Value = 85;
                applyColor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(combo.IsEnabled);
                DlssFeedbackTests.PumpUntil(() => vib.IsEnabled && ((TextBlock)page.FindName("VibStateText")).Text.Contains("85"));
                Assert.Equal(85, fixture.Levels["B"]);
                Assert.Equal(30, fixture.Levels["A"]);
                fixture.Displays[0] = fixture.Displays[0] with { IsPrimary = false };
                fixture.Displays[1] = fixture.Displays[1] with { IsPrimary = true };
                ((Button)page.FindName("DisplayRefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && combo.Text.Contains("Screen B"));
                ((ComboBox)color.FindName("ColorModeCombo")).SelectedIndex = 1;
                ((ComboBox)color.FindName("IccPresetCombo")).SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => applyColor.IsEnabled);
                applyColor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && ((TextBlock)page.FindName("IccCurrentText")).Text.Contains("Vivid"));
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
                DlssFeedbackTests.PumpUntil(() => ((Button)color.FindName("ColorRestoreButton")).IsEnabled);
                ((Button)color.FindName("ColorRestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => vib.IsEnabled && ((Slider)page.FindName("VibSlider")).Value == 60);
                DlssFeedbackTests.PumpUntil(() => icc.IsEnabled && IccFilterService.BackupTargets().Count == 0);
                Assert.Empty(fixture.Profiles["B"]);
                Assert.Empty(IccFilterService.BackupTargets());
                Assert.Empty(DigitalVibranceService.BackupTargets());
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                confirm?.Stop(); window?.Close();
                if (window is not null) window.Content = null;
                application?.Shutdown(); UserDataPaths.RootOverride = null; DialogService.PrepareWindowForTest = null;
                // WIC color contexts release immutable preview snapshot handles via finalization.
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
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
