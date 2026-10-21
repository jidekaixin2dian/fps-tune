using System.Windows;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class MaintenanceView
{
    private readonly MemoryCleanupService _memory;
    private static string MemorySize(ulong bytes) => $"{bytes / (1024d * 1024 * 1024):F2} GiB";
    private void ShowMemory(MemorySnapshot snapshot)
    {
        MemoryAvailable.Text = Str.T("Str.MemoryAvailable", MemorySize(snapshot.Available), MemorySize(snapshot.Total));
        MemoryUsage.Value = snapshot.Total == 0 ? 0 : 100 * (1 - snapshot.Available / (double)snapshot.Total);
        MemoryStandby.Text = snapshot.Standby is { } total && snapshot.LowPriorityStandby is { } low
            ? Str.T("Str.MemoryStandbySummary", Size((long)total), Size((long)low)) : snapshot.LowPriorityStandby is { } bytes
            ? Str.T("Str.MemoryStandby", Size((long)bytes)) : Str.T("Str.MemoryStandbyUnknown");
    }
    private async void MemoryClean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshotBusy) return;
        if (_memory.IsBusy || GamePreparationService.IsRunning) { MemoryResult.Text = Str.T("Str.MemoryBusy"); return; }
        if (!_memory.IsAdministrator)
        {
            if (DialogService.Confirm(Str.T("Str.NeedsAdmin"), Str.T("Str.MemoryAdmin"), confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
            return;
        }
        var combined = MemoryMode.SelectedIndex == 2;
        var targets = SelectedMemoryApps();
        if (combined && !ConfirmMemoryApps(targets)) return;
        _busy = true;
        var allPriorities = MemoryMode.SelectedIndex >= 1;
        SetMemoryBusy(true);
        MemoryResult.Text = Str.T("Str.MemoryCleaning");
        IReadOnlyList<MemoryProcessResult>? apps = null;
        try
        {
            var result = await Task.Run(() => _memory.Clean(allPriorities, combined ? () => apps = _background.Trim(targets) : null));
            if (result.After is { } after)
            {
                ShowMemory(after);
                MemoryResult.Text = result.Before.Standby is { } beforeCache && after.Standby is { } afterCache
                    ? Str.T("Str.MemoryCacheChange", Size((long)beforeCache), Size((long)afterCache), MemorySize(result.Before.Available), MemorySize(after.Available))
                    : Str.T("Str.MemoryCleaned", MemorySize(result.Before.Available), MemorySize(after.Available));
            }
            else MemoryResult.Text = Str.T("Str.MemoryCleanedReadFailed", PrivacyScrub.Sanitize(result.ReadError ?? ""));
        }
        catch (Exception ex) { MemoryResult.Text = Str.T("Str.MemoryFailed", PrivacyScrub.Sanitize(ex.Message)); }
        finally
        {
            if (apps is not null) ShowMemoryAppsResult(apps);
            _busy = false; SetMemoryBusy(false);
            if (combined) await RefreshMemoryAppsAsync();
        }
    }
}
