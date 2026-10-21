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
    private CancellationTokenSource? _download;
    private string _phase = "";
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
        InstallButton.SetResourceReference(ContentProperty, "Str.UpdateNow");
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
        await RunInstallAsync(false);
    }
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _download?.Cancel();
    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_recovery is null || !DialogService.Confirm(Str.T("Str.UpdateRollback"), Str.T("Str.UpdateRollbackConfirm"))) return;
        await RunInstallAsync(true);
    }
    private async Task RunInstallAsync(bool rollback)
    {
        if (_running) return;
        if (App.SessionService.IsRunning || ExperimentRunner.IsRunning)
        {
            StatusText.Text = Str.T("Str.UpdateSessionBusy");
            return;
        }
        InstallButton.IsEnabled = false;
        RollbackButton.IsEnabled = false;
        _running = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = cancellation;
        CancelDownloadButton.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        StatusText.Text = Str.T("Str.UpdateDownloading");
        try
        {
            void Stage(string text) => Dispatcher.Invoke(() =>
            { _phase = text; StatusText.Text = text; DownloadProgress.IsIndeterminate = true; });
            void Progress(double value) => Dispatcher.Invoke(() =>
            {
                DownloadProgress.IsIndeterminate = value < 0;
                if (value >= 0) DownloadProgress.Value = Math.Clamp(value, 0, 100);
                StatusText.Text = value < 0 ? _phase : _phase + " " + value.ToString("0") + "%";
            });
            if (rollback) await UpdateInstaller.RollbackAsync(_recovery!, Progress, cancellation.Token, Stage);
            else if (_recovery is not null) await UpdateInstaller.RetryAsync(_recovery, Progress, cancellation.Token, Stage);
            else await UpdateInstaller.InstallAsync(_info, Progress, cancellation.Token, Stage);
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
            _download = null;
            InstallButton.IsEnabled = _recovery is null || !UpdateRecovery.Busy(_recovery);
            RollbackButton.IsEnabled = _recovery is { Mode: "upgrade" } && UpdateRecovery.CanRollback(_recovery) && !UpdateRecovery.Busy(_recovery);
            DownloadProgress.Visibility = Visibility.Collapsed;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
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
