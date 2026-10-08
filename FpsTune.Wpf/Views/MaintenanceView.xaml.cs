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
    public MaintenanceView() => InitializeComponent();
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
