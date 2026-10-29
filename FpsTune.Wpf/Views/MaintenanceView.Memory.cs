using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class MaintenanceView
{
    private readonly MemoryCleanupService _memory;
    private CancellationTokenSource? _memoryCancellation;
    private int _memoryGeneration;
    private static string MemorySize(ulong bytes) => $"{bytes / (1024d * 1024 * 1024):F2} GiB";
    private void ShowMemory(MemorySnapshot snapshot)
    {
        MemoryAvailable.Text = Str.T("Str.MemoryAvailable", MemorySize(snapshot.Available), MemorySize(snapshot.Total));
        MemoryUsage.Visibility = Visibility.Visible;
        MemoryUsage.Value = snapshot.Total == 0 ? 0 : Math.Clamp(100 * (1 - snapshot.Available / (double)snapshot.Total), 0, 100);
        MemoryPercent.Text = $"{MemoryUsage.Value:F0}%";
        MemoryCommit.Text = snapshot.Committed is { } used && snapshot.CommitLimit is { } limit
            ? Str.T("Str.MemoryCommit", MemorySize(used), MemorySize(limit)) : Str.T("Str.MemoryCommitUnknown");
        MemoryStandby.Text = snapshot.Standby is { } total && snapshot.LowPriorityStandby is { } low
            ? Str.T("Str.MemoryStandbySummary", Size((long)total), Size((long)low)) : snapshot.LowPriorityStandby is { } bytes
            ? Str.T("Str.MemoryStandby", Size((long)bytes)) : Str.T("Str.MemoryStandbyUnknown");
    }
    private void ShowMemoryUnavailable(string error)
    {
        MemoryAvailable.Text = Str.T("Str.MemoryReadFailed", PrivacyScrub.Sanitize(error));
        MemoryPercent.Text = "—"; MemoryCommit.Text = MemoryStandby.Text = "";
        MemoryUsage.Visibility = Visibility.Collapsed;
    }
    private async void MemoryRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMemoryLiveAsync();
    private async void MemoryClean_Click(object sender, RoutedEventArgs e)
        => await RunMemoryAsync(MemoryMode.SelectedIndex == 0 ? MemoryCleanupMode.LowStandby : MemoryCleanupMode.Standby);
    private async void MemoryCombined_Click(object sender, RoutedEventArgs e) => await RunMemoryAsync(MemoryCleanupMode.Combined);
    private async void MemoryFull_Click(object sender, RoutedEventArgs e) => await RunMemoryAsync(MemoryCleanupMode.Full);
    private void MemoryCancel_Click(object sender, RoutedEventArgs e)
    {
        _memoryCancellation?.Cancel(); MemoryCancel.IsEnabled = false;
        MemoryStage.Text = Str.T("Str.MemoryStopping");
    }
    private async Task RunMemoryAsync(MemoryCleanupMode mode)
    {
        if (_busy || _snapshotBusy || _appsReading) return;
        if (_memory.IsBusy || GamePreparationService.IsRunning) { MemoryResult.Text = Str.T("Str.MemoryBusy"); return; }
        if (mode != MemoryCleanupMode.WorkingSets && !_memory.IsAdministrator)
        {
            if (DialogService.Confirm(Str.T("Str.NeedsAdmin"), Str.T("Str.MemoryAdmin"), confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
            return;
        }
        var targets = SelectedMemoryApps();
        var hasApps = mode is MemoryCleanupMode.WorkingSets or MemoryCleanupMode.Combined or MemoryCleanupMode.Full;
        if (mode == MemoryCleanupMode.Full)
        {
            _busy = true; SetMemoryBusy(true); MemoryResult.Text = Str.T("Str.MemoryFindingApps");
            try { targets = (await Task.Run(_background.Read)).ToArray(); }
            catch (Exception ex) { MemoryResult.Text = Str.T("Str.MemoryFailed", PrivacyScrub.Sanitize(ex.Message)); return; }
            finally { _busy = false; SetMemoryBusy(false); }
            MemoryResult.Text = "";
            if (!IsLoaded || !ProcessesSection.IsVisible) return;
            if (!DialogService.Confirm(Str.T("Str.MemoryFullAction"),
                Str.T("Str.MemoryFullConfirm", targets.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), targets.Length),
                confirmText: Str.T("Str.MemoryFullAction"))) return;
        }
        else if (hasApps && !ConfirmMemoryApps(targets)) { MemoryResult.Text = MemoryAppsResult.Text; return; }
        _busy = true; _memoryGeneration++; SetMemoryBusy(true);
        using var cancellation = new CancellationTokenSource(); _memoryCancellation = cancellation;
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void UpdateElapsed() => MemoryElapsed.Text = Str.T("Str.MemoryElapsed", clock.Elapsed.TotalSeconds.ToString("F1"));
        timer.Tick += (_, _) => UpdateElapsed(); timer.Start(); UpdateElapsed();
        MemoryResult.Text = ""; if (hasApps) MemoryAppsResult.Text = "";
        MemoryStage.Text = Str.T("Str.MemoryMeasureStage"); MemoryCancel.IsEnabled = true;
        MemoryProgressPanel.Visibility = Visibility.Visible;
        _ = MemoryProgressPanel.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        { if (_busy && ProcessesSection.IsVisible) MemoryProgressPanel.BringIntoView(); }));
        var progress = new Progress<MemoryCleanupProgress>(p =>
        {
            if (!_busy || cancellation.IsCancellationRequested) return;
            MemoryStage.Text = MemoryCleanupSummary.Stage(p);
        });
        try
        {
            var result = await Task.Run(() => _memory.Run(mode, targets, _background, progress, cancellation.Token));
            if (result.After is { } after) ShowMemory(after);
            else ShowMemoryUnavailable(result.ReadError ?? "");
            if (hasApps) ShowMemoryAppsResult(result.Apps);
            MemoryResult.Text = MemoryCleanupSummary.Format(result, targets.Length, hasApps,
                mode != MemoryCleanupMode.WorkingSets, clock.Elapsed.TotalSeconds);
            if (ProcessesSection.IsVisible) MemoryResult.BringIntoView();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { MemoryResult.Text = Str.T("Str.MemoryStopped", clock.Elapsed.TotalSeconds.ToString("F1")); }
        catch (Exception ex) { MemoryResult.Text = Str.T("Str.MemoryFailed", PrivacyScrub.Sanitize(ex.Message)); }
        finally
        {
            timer.Stop(); _memoryCancellation = null; MemoryProgressPanel.Visibility = Visibility.Collapsed;
            _busy = false; SetMemoryBusy(false);
            if (hasApps) await RefreshMemoryAppsAsync();
        }
    }
}
