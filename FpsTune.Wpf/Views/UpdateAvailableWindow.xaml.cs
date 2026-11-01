using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class UpdateAvailableWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly Action<string> _openLink;
    private readonly UpdateMirrors.BackupDownload? _backup;
    private CancellationTokenSource? _download;
    private bool _handedOff;
    private string _phase = "";
    internal bool IsRunning => _download is not null;
    internal Func<UpdateInfo, Action<double>, CancellationToken, Action<string>?, Task> InstallAction { get; set; } = UpdateInstaller.InstallAsync;
    internal Action FinishInstallation { get; set; } = () => Application.Current.Shutdown();
    public UpdateAvailableWindow(UpdateInfo info) : this(info, ProductLinks.Open) { }
    internal UpdateAvailableWindow(UpdateInfo info, Action<string> openLink)
    {
        _info = info; _openLink = openLink;
        InitializeComponent();
        VersionText.Text = $"{UpdateService.DisplayVersion} → {info.ReleaseTag ?? info.Version}";
        NotesText.Text = ReleaseNotesText.Format(info.Notes);
        _backup = UpdateMirrors.FindBackupLink(info.Notes);
        BackupDownloadButton.Visibility = _backup is null ? Visibility.Collapsed : Visibility.Visible;
        if (_backup?.Password is { } password)
        {
            BackupPasswordText.Text = Str.T("Str.UpdateBackupPassword", password);
            BackupPasswordText.Visibility = Visibility.Visible;
        }
        Closing += (_, e) =>
        {
            if (IsRunning && !_handedOff) { e.Cancel = true; _download!.Cancel(); }
        };
        Closed += (_, _) => { if (!_handedOff) _download?.Cancel(); };
    }
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        try { _openLink(ProductLinks.ReleasePage(_info)); }
        catch { StatusText.Text = Str.T("Str.CannotOpenLink"); return; }
        Close();
    }
    private void BackupDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_backup is null) return;
        try
        {
            // 网盘页面会要求输入访问密码：点一次就把密码放进剪贴板，省一步手抄。
            if (_backup.Password is { } password)
            {
                Clipboard.SetText(password);
                StatusText.Text = Str.T("Str.UpdateBackupPasswordCopied", password);
            }
            _openLink(_backup.Url);
        }
        catch { StatusText.Text = Str.T("Str.CannotOpenLink"); }
    }
    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync();
    internal async Task InstallAsync()
    {
        if (IsRunning) return;
        if (App.SessionService.IsRunning || ExperimentRunner.IsRunning)
        { StatusText.Text = Str.T("Str.UpdateSessionBusy"); return; }
        using var cancellation = new CancellationTokenSource();
        _download = cancellation;
        _handedOff = false;
        SetBusy(true);
        void Stage(string text) => Dispatcher.Invoke(() =>
        { _phase = text; StatusText.Text = text; DownloadProgress.IsIndeterminate = true; });
        void Progress(double value) => Dispatcher.Invoke(() =>
        {
            DownloadProgress.IsIndeterminate = value < 0;
            if (value >= 0) DownloadProgress.Value = Math.Clamp(value, 0, 100);
            StatusText.Text = value < 0 ? _phase : _phase + " " + value.ToString("0") + "%";
        });
        try
        {
            Stage(Str.T("Str.UpdateDownloading"));
            await InstallAction(_info, Progress, cancellation.Token, Stage);
            _handedOff = true;
            StatusText.Text = Str.T("Str.UpdatePreparingInstall");
            FinishInstallation();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { StatusText.Text = Str.T("Str.UpdateCancelled"); }
        catch (Exception ex)
        { StatusText.Text = Str.T("Str.UpdateFailed", OperationDiagnostics.Scrub(ex.Message)); }
        finally
        {
            _download = null;
            if (!_handedOff) SetBusy(false);
        }
    }
    private void SetBusy(bool busy)
    {
        InstallButton.IsEnabled = DownloadPageButton.IsEnabled = LaterButton.IsEnabled = BackupDownloadButton.IsEnabled = !busy;
        CancelDownloadButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = busy;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _download?.Cancel();
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
