using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ProductNotificationUiTests
{
    [Fact]
    public void Centered_community_opt_out_is_independent_from_update_and_manual_feedback_stays_available()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ProductNotificationUiTests)); return; }
        RunUi(app =>
        {
            var key = @"Software\FpsTune\TestPreferences\" + Guid.NewGuid().ToString("N");
            var window = new MainWindow { CommunityPreferenceStore = new CommunityPreferences(key) };
            window.NavigateTo("opt"); app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            var communities = 0; var updates = 0;
            try
            {
                window.ShowGuideCard();
                timer.Tick += (_, _) =>
                {
                    if (app.Windows.OfType<CommunityWindow>().FirstOrDefault(w => w.IsVisible) is { } community)
                    {
                        communities++;
                        RenderThemes((FrameworkElement)community.Content, "community");
                        var checkbox = (CheckBox)community.FindName("NeverRemindCheck");
                        checkbox.IsChecked = true;
                        RenderThemes((FrameworkElement)community.Content, "community-checked");
                        checkbox.IsChecked = false;
                        if (communities == 1)
                        {
                            ((CheckBox)community.FindName("NeverRemindCheck")).IsChecked = true;
                            window.ShowUpdateCard(ProductNotificationTests.Info());
                        }
                        community.Close();
                    }
                    else if (app.Windows.OfType<UpdateAvailableWindow>().FirstOrDefault(w => w.IsVisible) is { } update)
                    {
                        updates++;
                        RenderThemes((FrameworkElement)update.Content, "update");
                        update.Close();
                    }
                };
                timer.Start();
                window.ShowCommunityPrompt();
                DlssFeedbackTests.PumpUntil(() => updates == 1);
                Assert.Equal(1, communities);
                Assert.True(new CommunityPreferences(key).IsSuppressed);
                Assert.Single(((StackPanel)window.FindName("NotificationStack")).Children.OfType<GuideCard>());
                window.ShowCommunityPrompt(automatic: true);
                Assert.Equal(1, communities);
                window.ShowCommunityPrompt(); Assert.Equal(2, communities);
                window.ShowUpdateCard(ProductNotificationTests.Info()); Assert.Equal(2, updates);
                window.NavigateTo("settings");
                var settings = (SettingsView)((ContentControl)window.FindName("PageHost")).Content;
                Assert.Equal(Visibility.Collapsed, ((StackPanel)settings.FindName("LanguageOptions")).Visibility);
                Assert.Equal(WindowStartupLocation.CenterOwner, new CommunityWindow(() => { }, _ => { }).WindowStartupLocation);
            }
            finally
            {
                timer.Stop(); window.Close();
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            }
        });
    }

    [Fact]
    public void Link_and_preference_failures_remain_visible_and_retryable_without_launching_external_programs()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ProductNotificationUiTests)); return; }
        RunUi(app =>
        {
            var openFails = true; var saveFails = true; var saves = 0; var links = new List<string>();
            var community = new CommunityWindow(
                () => { if (saveFails) throw new IOException("controlled preference failure"); saves++; },
                link => { if (openFails) throw new IOException("controlled link failure"); links.Add(link); });
            DlssFeedbackTests.PrepareBackgroundWindow(community); community.Show();
            try
            {
                ((CheckBox)community.FindName("NeverRemindCheck")).IsChecked = true;
                Click(community, "JoinButton");
                Assert.True(community.IsVisible); Assert.Empty(links); Assert.Equal(0, saves);
                Assert.Equal(Str.T("Str.CannotOpenLink"), ((TextBlock)community.FindName("StatusText")).Text);
                openFails = false; Click(community, "JoinButton");
                Assert.True(community.IsVisible); Assert.Equal([ProductLinks.CommunityUrl], links);
                Assert.Equal(Str.T("Str.CommunitySaveFailed"), ((TextBlock)community.FindName("StatusText")).Text);
                saveFails = false; Click(community, "LaterButton");
                Assert.False(community.IsVisible); Assert.Equal(1, saves);
            }
            finally { ((CheckBox)community.FindName("NeverRemindCheck")).IsChecked = false; community.Close(); }
            var update = new UpdateAvailableWindow(ProductNotificationTests.Info(), link =>
            {
                if (openFails) throw new IOException("controlled browser failure");
                links.Add(link);
            });
            DlssFeedbackTests.PrepareBackgroundWindow(update); update.Show();
            try
            {
                openFails = true; Click(update, "DownloadPageButton");
                Assert.True(update.IsVisible);
                Assert.Equal(Str.T("Str.CannotOpenLink"), ((TextBlock)update.FindName("StatusText")).Text);
                openFails = false; Click(update, "DownloadPageButton");
                Assert.False(update.IsVisible); Assert.Equal(ProductNotificationTests.Info().Url, links.Last());
                Assert.Equal(2, links.Count);
            }
            finally { update.Close(); }
            var blocked = new UpdateAvailableWindow(ProductNotificationTests.Info("https://example.com"), links.Add);
            DlssFeedbackTests.PrepareBackgroundWindow(blocked); blocked.Show();
            try { Click(blocked, "DownloadPageButton"); Assert.True(blocked.IsVisible); Assert.Equal(2, links.Count); }
            finally { blocked.Close(); }
        });
    }

    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void RunUi(Action<Application> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            var oldRoot = UserDataPaths.RootOverride; var oldLowSpec = UiPerformance.LowSpec;
            try
            {
                UserDataPaths.RootOverride = Path.Combine(Output(), "test-data", Guid.NewGuid().ToString("N"));
                UiPerformance.LowSpec = true;
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = DlssFeedbackTests.LoadResources() };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                LangService.Apply(LangService.ZhCn);
                DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
                SettingsService.Current.HotkeyEnabled = false;
                test(app);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                app?.Shutdown(); DialogService.PrepareWindowForTest = null;
                UserDataPaths.RootOverride = oldRoot; UiPerformance.LowSpec = oldLowSpec;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Product notification UI regression did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static string Output()
    {
        var path = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "community-update", "ui"); Directory.CreateDirectory(path); return path;
    }
    private static void RenderThemes(FrameworkElement element, string name)
    {
        foreach (var theme in new[] { "dark", "light" })
        {
            ThemeManager.SetMode(theme); element.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(element);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bitmap.Render(visual);
            using var stream = File.Create(Path.Combine(Output(), name + "-" + theme + ".png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        }
    }
}
