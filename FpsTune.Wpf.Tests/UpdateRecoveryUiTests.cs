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
public sealed class UpdateRecoveryUiTests
{
    [Fact]
    public void Actual_recovery_card_retries_without_losing_failure_actions_and_renders_both_languages()
    {
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        {
            DlssFeedbackTests.RunIsolated(testType: typeof(UpdateRecoveryUiTests));
            return;
        }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? application = null;
            Window? window = null;
            using var fixture = new UpdateRecoveryTests();
            try
            {
                UpdateInstaller.InstallAsync(UpdateRecoveryTests.Info(), _ => { }, default).GetAwaiter().GetResult();
                var attempt = UpdateRecovery.Recent()!;
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = DlssFeedbackTests.LoadResources() };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
                var card = new UpdateCard(attempt);
                window = new Window { Content = card, Width = 440, SizeToContent = SizeToContent.Height,
                    Background = (Brush)application.FindResource("ConsoleBackgroundBrush") };
                application.MainWindow = window;
                window.Show();
                Assert.Equal(Str.T("Str.UpdateRecovery"), card.Notification.NotificationTitle);
                Assert.True(((Button)card.FindName("RollbackButton")).IsEnabled);
                Assert.True(((Button)card.FindName("ProtectedCopyButton")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)card.FindName("RecoveryActions")).Visibility);
                var status = (TextBlock)card.FindName("StatusText");
                Assert.Contains("4", status.Text);
                UpdateRecovery.WorkerOverride = (_, _) => throw new IOException("fixture handover failure");
                ((Button)card.FindName("InstallButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => !card.IsRunning && status.Text.Contains("fixture handover failure"));
                Assert.True(window.IsVisible);
                Assert.True(((Button)card.FindName("InstallButton")).IsEnabled);
                Assert.True(((Button)card.FindName("ProtectedCopyButton")).IsEnabled);
                Assert.True(((Button)card.FindName("RollbackButton")).IsEnabled);
                Assert.Equal("failed", UpdateRecovery.Recent()!.Status);
                Assert.NotNull(UpdateRecovery.ProtectedCopy(attempt));
                foreach (var language in new[] { LangService.ZhCn, LangService.EnUs })
                {
                    LangService.Apply(language);
                    card = new UpdateCard(attempt);
                    window.Content = card;
                    window.UpdateLayout();
                    Assert.Equal(Str.T("Str.UpdateRetry"), ((Button)card.FindName("InstallButton")).Content);
                    Assert.DoesNotContain("!Str.", ((TextBlock)card.FindName("NotesText")).Text);
                    Render(card, "recovery-" + language + ".png");
                }
                var missing = new UpdateCard(attempt with { PreviousPackageId = null });
                Assert.False(((Button)missing.FindName("RollbackButton")).IsEnabled);
                Assert.Contains(Str.T("Str.UpdateRollbackUnavailable"), ((TextBlock)missing.FindName("NotesText")).Text);
                var success = new UpdateCard(attempt with { Status = "succeeded" });
                Assert.Equal(Visibility.Collapsed, ((Button)success.FindName("InstallButton")).Visibility);
            }
            catch (Exception ex) { error = ex; }
            finally { window?.Close(); application?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Update recovery UI did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Render(FrameworkElement element, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "update-recovery", "ui");
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
