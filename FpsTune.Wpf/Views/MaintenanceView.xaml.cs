using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class MaintenanceView : UserControl
{
    private readonly ShaderCacheService _service = ShaderCacheService.ForCurrentUser();
    private ShaderCacheService.Preview? _preview;
    private bool _busy;
    private sealed record FolderRow(string Name, string Amount, string Path);
    public MaintenanceView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshSnapshotAsync();
    }
    private bool _snapshotBusy;
    private sealed record DriveRow(string Name, string Amount, double UsedPercent, bool LowSpace);
    private sealed record ProcessRow(string Name, int Pid, string Amount);
    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (StorageSection is null || sender is not RadioButton { Tag: string tag }) return;
        var sections = new[] { StorageSection, ProcessesSection, TroubleshootSection };
        for (var i = 0; i < sections.Length; i++)
            sections[i].Visibility = tag == i.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSnapshotAsync();
    private async Task RefreshSnapshotAsync()
    {
        if (_snapshotBusy) return;
        _snapshotBusy = true;
        RefreshButton.IsEnabled = false;
        SnapshotStatus.Text = Str.T("Str.MaintenanceLoading");
        try
        {
            var snapshot = await Task.Run(() => (Drives: MaintenanceService.ReadDrives(), Processes: MaintenanceService.ReadProcesses()));
            Drives.ItemsSource = snapshot.Drives.Items.Select(d => new DriveRow(d.Name,
                Str.T("Str.MaintenanceFree", $"{d.Available / (1024d * 1024 * 1024):F1} GiB", $"{d.Total / (1024d * 1024 * 1024):F1} GiB"), d.UsedPercent, d.LowSpace));
            Processes.ItemsSource = snapshot.Processes.Items.Select(p => new ProcessRow(p.Name, p.Pid, Size(p.WorkingSet)));
            NoDrives.Visibility = snapshot.Drives.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoProcesses.Visibility = snapshot.Processes.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SnapshotStatus.Text = Str.T("Str.MaintenanceReady", DateTime.Now.ToString("HH:mm:ss"), snapshot.Drives.Unavailable + snapshot.Processes.Unavailable);
        }
        catch (Exception ex) { SnapshotStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _snapshotBusy = false; RefreshButton.IsEnabled = true; }
    }
    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        try
        {
            MaintenanceService.Open(id);
            ToolStatus.Text = Str.T("Str.MaintenanceOpened", button.ToolTip ?? button.Content);
        }
        catch (Exception ex) { ToolStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private static string Size(long bytes) => $"{bytes / (1024d * 1024):F1} MiB";
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; ScanButton.IsEnabled = CleanButton.IsEnabled = false;
        _preview = null;
        Summary.Text = Str.T("Str.CacheScanning");
        try
        {
            _preview = await Task.Run(_service.Scan);
            Summary.Text = Str.T("Str.CacheSummary", _preview.Entries.Count, Size(_preview.Bytes));
            Folders.ItemsSource = _preview.Folders.Select(f => new FolderRow(Path.GetFileName(f.Path), Str.T("Str.CacheSummary", f.Files, Size(f.Bytes)), f.Path));
            Skipped.Text = _preview.Truncated ? Str.T("Str.CacheTruncated") : Str.T("Str.CacheSkipped", _preview.Skipped.Count);
            CleanButton.IsEnabled = !_preview.Truncated && _preview.Entries.Count > 0;
        }
        catch (Exception ex) { Summary.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _busy = false; ScanButton.IsEnabled = true; }
    }
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _preview is null) return;
        if (App.SessionService.IsRunning || ExperimentRunner.IsRunning) { Summary.Text = Str.T("Str.CacheBusy"); return; }
        if (!DialogService.Confirm(Str.T("Str.ShaderCache"),
            Str.T("Str.CacheConfirm", _preview.Entries.Count, Size(_preview.Bytes)), confirmText: Str.T("Str.CacheClean"))) return;
        _busy = true; ScanButton.IsEnabled = CleanButton.IsEnabled = false;
        var preview = _preview; _preview = null;
        try
        {
            var result = await Task.Run(() => _service.Clean(preview));
            Summary.Text = Str.T("Str.CacheCleaned", result.Deleted, Size(result.Bytes), result.Skipped.Count);
            Folders.ItemsSource = null;
            Skipped.Text = result.Skipped.Count == 0 ? "" : Str.T("Str.CacheSkipped", result.Skipped.Count);
        }
        catch (Exception ex) { Summary.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _busy = false; ScanButton.IsEnabled = true; }
    }
}
