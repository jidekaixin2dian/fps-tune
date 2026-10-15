using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;

namespace FpsTune.Wpf.Views;

public partial class UpdateCard : UserControl
{
    private readonly UpdateInfo _info;
    private UpdateAttempt? _recovery;
    private bool _running;
    private readonly CancellationTokenSource _lifetime = new();
    public NotificationCard Notification => Card;
    internal bool IsRunning => _running;
    public UpdateCard(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        Card.SetHeader(Str.T("Str.UpdateAvailable"), $"{UpdateService.DisplayVersion} → {info.ReleaseTag ?? info.Version}");
        Card.SetResourceReference(NotificationCard.NotificationTitleProperty, "Str.UpdateAvailable");
        NotesText.Text = info.Notes;
        StatusText.SetResourceReference(TextBlock.TextProperty, "Str.UpdateOptional");
        InstallButton.SetResourceReference(ContentProperty, "Str.ReleaseDownloadPage");
    }
    internal UpdateCard(UpdateAttempt attempt) : this(attempt.Target) => ShowRecovery(attempt);
    private void ShowRecovery(UpdateAttempt attempt)
    {
        _recovery = attempt;
        Card.SetHeader(Str.T("Str.UpdateRecovery"), $"{attempt.FromVersion} → {attempt.Target.ReleaseTag}");
        Card.SetResourceReference(NotificationCard.NotificationTitleProperty, "Str.UpdateRecovery");
        NotesText.SetResourceReference(TextBlock.TextProperty, "Str.UpdateProtectedHint");
        StatusText.Text = Str.T(attempt.Status switch
        {
            "succeeded" => "Str.UpdateSucceeded",
            "cancelled" => "Str.UpdateInstallationCancelled",
            "prepared" or "waiting-for-exit" or "installing" => "Str.UpdateInProgress",
            "interrupted" => "Str.UpdateInterrupted",
            "awaiting-startup" => "Str.UpdateAwaitingVerification",
            _ => "Str.UpdateRecoveryFailed"
        });
        if (attempt.ExitCode is not null && attempt.Status is "failed" or "cancelled")
            StatusText.Text += " " + Str.T("Str.UpdateExitCode", attempt.ExitCode);
        RecoveryActions.Visibility = Visibility.Visible;
        InstallButton.SetResourceReference(ContentProperty, "Str.UpdateRetry");
        InstallButton.Visibility = attempt.Status == "succeeded" ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.IsEnabled = !UpdateRecovery.Busy(attempt);
        RollbackButton.IsEnabled = attempt.Mode == "upgrade" && UpdateRecovery.CanRollback(attempt) && !UpdateRecovery.Busy(attempt);
        try { ProtectedCopyButton.IsEnabled = UpdateRecovery.ProtectedCopy(attempt) is not null; }
        catch { ProtectedCopyButton.IsEnabled = false; }
        if (!UpdateRecovery.CanRollback(attempt) && attempt.Status != "succeeded")
            NotesText.Text = Str.T("Str.UpdateProtectedHint") + "\n" + Str.T("Str.UpdateRollbackUnavailable");
    }
    public void Cancel() => _lifetime.Cancel();
    private void Later_Click(object sender, RoutedEventArgs e) => Card.SetExpanded(false);
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_recovery is not null) { await RunInstallAsync(false); return; }
        try { ProductLinks.Open(ProductLinks.ReleasePage(_info)); }
        catch { StatusText.Text = Str.T("Str.CannotOpenLink"); }
    }
    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_recovery is null || !DialogService.Confirm(Str.T("Str.UpdateRollback"), Str.T("Str.UpdateRollbackConfirm"))) return;
        await RunInstallAsync(true);
    }
    private async Task RunInstallAsync(bool rollback)
    {
        if (_running || _recovery is null) return;
        if (App.SessionService.IsRunning || ExperimentRunner.IsRunning)
        {
            StatusText.Text = Str.T("Str.UpdateSessionBusy");
            return;
        }
        InstallButton.IsEnabled = false;
        RollbackButton.IsEnabled = false;
        _running = true;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = Str.T("Str.UpdateDownloading");
        try
        {
            void Progress(double value) => Dispatcher.Invoke(() => DownloadProgress.Value = value);
            if (rollback) await UpdateInstaller.RollbackAsync(_recovery!, Progress, _lifetime.Token);
            else await UpdateInstaller.RetryAsync(_recovery, Progress, _lifetime.Token);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { StatusText.Text = Str.T("Str.UpdateCancelled"); }
        catch (Exception ex)
        {
            var recent = UpdateRecovery.Recent();
            if (recent is not null && recent.Target.ReleaseTag == _info.ReleaseTag) ShowRecovery(recent);
            StatusText.Text = Str.T("Str.UpdateFailed", OperationDiagnostics.Scrub(ex.Message));
        }
        finally
        {
            _running = false;
            InstallButton.IsEnabled = _recovery is null || !UpdateRecovery.Busy(_recovery);
            RollbackButton.IsEnabled = _recovery is { Mode: "upgrade" } && UpdateRecovery.CanRollback(_recovery) && !UpdateRecovery.Busy(_recovery);
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }
    private void ProtectedCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _recovery is null ? null : UpdateRecovery.ProtectedCopy(_recovery);
            if (path is null) throw new InvalidOperationException(Str.T("Str.UpdateCopyUnavailable"));
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
        }
        catch (Exception ex) { StatusText.Text = Str.T("Str.UpdateFailed", OperationDiagnostics.Scrub(ex.Message)); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var path = DiagnosticReportExporter.Export();
        if (path is not null) DialogService.Info(Str.T("Str.DiagnosticReport"), Str.T("Str.DiagExportedBody", path));
    }
}
