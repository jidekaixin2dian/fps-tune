using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Views;

public partial class MaintenanceView
{
    private readonly BackgroundMemoryService _background = new();
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _memoryReading, _appsReading, _appsSyncing;
    private sealed class MemoryAppRow(string path, IReadOnlyList<MemoryProcessTarget> targets, bool selected)
    {
        public string Path { get; } = path;
        public IReadOnlyList<MemoryProcessTarget> Targets { get; } = targets;
        public bool Selected { get; set; } = selected;
        public string Label => Str.T("Str.MemoryAppLabel", System.IO.Path.GetFileName(Path), Targets.Count, Size(Targets.Sum(t => t.WorkingSet)));
    }
    private void InitializeMemory()
    {
        var policy = SettingsService.Current.MemoryAutoCleanup.Normalize();
        MemoryAutoEnabled.IsChecked = policy.Enabled;
        SelectValue(MemoryThreshold, policy.ThresholdMiB);
        SelectValue(MemoryCooldown, policy.CooldownMinutes);
        _memoryTimer.Tick += async (_, _) => await RefreshMemoryLiveAsync();
        Loaded += async (_, _) => { _memoryTimer.Start(); if (ProcessesSection.IsVisible) await RefreshMemoryAppsAsync(); };
        Unloaded += (_, _) => _memoryTimer.Stop();
    }
    private static void SelectValue(ComboBox combo, int value)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == value.ToString());
        if (combo.SelectedIndex < 0)
        {
            var item = new ComboBoxItem { Tag = value.ToString(), Content = value + (combo.Name == "MemoryThreshold" ? " MiB" : " min") };
            combo.Items.Add(item); combo.SelectedItem = item;
        }
    }
    private async Task RefreshMemoryLiveAsync()
    {
        if (!IsLoaded || !ProcessesSection.IsVisible || _memoryReading || _busy || _snapshotBusy) return;
        _memoryReading = true;
        try
        {
            var snapshot = await Task.Run(_memory.Read);
            if (!IsLoaded) return;
            ShowMemory(snapshot);
            MemoryAutoStatus.Text = App.MemoryAutomation?.LastStatus ?? Str.T(SettingsService.Current.MemoryAutoCleanup.Enabled
                ? "Str.MemoryAutoWaiting" : "Str.MemoryAutoOff");
        }
        catch (Exception ex) { MemoryAvailable.Text = Str.T("Str.MemoryReadFailed", PrivacyScrub.Sanitize(ex.Message)); }
        finally { _memoryReading = false; }
    }
    private async void MemoryAppsRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMemoryAppsAsync();
    private async Task RefreshMemoryAppsAsync()
    {
        if (_appsReading || _busy) return;
        _appsReading = true; MemoryAppsRefresh.IsEnabled = false;
        try
        {
            var targets = await Task.Run(_background.Read);
            var saved = SettingsService.Current.MemoryBackgroundApps.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = targets.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(t => t.WorkingSet)).Select(g => new MemoryAppRow(g.Key, g.ToArray(), saved.Contains(g.Key))).ToArray();
            _appsSyncing = true;
            try { MemoryApps.ItemsSource = rows; }
            finally { _appsSyncing = false; }
            MemoryAppsStatus.Text = Str.T("Str.MemoryAppCount", rows.Length);
        }
        catch (Exception ex) { MemoryAppsStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _appsReading = false; MemoryAppsRefresh.IsEnabled = true; }
    }
    private void MemoryAppSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (_appsSyncing || sender is not CheckBox { DataContext: MemoryAppRow row } check) return;
        row.Selected = check.IsChecked == true;
        var settings = SettingsService.Current;
        var before = settings.MemoryBackgroundApps;
        var selected = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (row.Selected) selected.Add(row.Path); else selected.Remove(row.Path);
        settings.MemoryBackgroundApps = selected.ToList();
        try { SettingsService.Save(settings); }
        catch (Exception ex)
        {
            settings.MemoryBackgroundApps = before;
            _appsSyncing = true;
            try { row.Selected = !row.Selected; check.IsChecked = row.Selected; }
            finally { _appsSyncing = false; }
            MemoryAppsStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
    }
    private MemoryProcessTarget[] SelectedMemoryApps() => MemoryApps.Items.Cast<MemoryAppRow>()
        .Where(r => r.Selected).SelectMany(r => r.Targets).ToArray();
    private bool ConfirmMemoryApps(IReadOnlyList<MemoryProcessTarget> targets)
    {
        if (targets.Count == 0) { MemoryAppsResult.Text = Str.T("Str.MemorySelectApps"); return false; }
        return DialogService.Confirm(Str.T("Str.MemoryBackgroundTitle"), Str.T("Str.MemoryTrimConfirm", targets.Count),
            confirmText: Str.T("Str.MemoryTrimSelected"));
    }
    private void ShowMemoryAppsResult(IReadOnlyList<MemoryProcessResult> results)
    {
        MemoryAppsResult.Text = string.Join("\n", results.Select(r => r.Error is not null
            ? Str.T("Str.MemoryAppFailed", r.Name, PrivacyScrub.Sanitize(r.Error))
            : Str.T("Str.MemoryAppDone", r.Name, Size(r.Before), r.After is { } bytes ? Size(bytes) : "—")));
    }
    private async void MemoryAppsTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshotBusy || _appsReading) return;
        var targets = SelectedMemoryApps();
        if (!ConfirmMemoryApps(targets)) return;
        _busy = true; SetMemoryBusy(true);
        try
        {
            var result = await Task.Run(() =>
            {
                using var gate = SystemMutationGate.Acquire();
                var before = _memory.Read(); var rows = _background.Trim(targets);
                MemorySnapshot? after = null; string? readError = null;
                try { after = _memory.Read(); } catch (Exception ex) { readError = ex.Message; }
                return (before, rows, after, readError);
            });
            ShowMemoryAppsResult(result.rows);
            if (result.after is { } after)
            { ShowMemory(after); MemoryResult.Text = Str.T("Str.MemoryCleaned", MemorySize(result.before.Available), MemorySize(after.Available)); }
            else MemoryResult.Text = Str.T("Str.MemoryCleanedReadFailed", PrivacyScrub.Sanitize(result.readError ?? ""));
        }
        catch (Exception ex) { MemoryAppsResult.Text = Str.T("Str.MemoryFailed", PrivacyScrub.Sanitize(ex.Message)); }
        finally { _busy = false; SetMemoryBusy(false); await RefreshMemoryAppsAsync(); }
    }
    private void SetMemoryBusy(bool busy)
    {
        MemoryMode.IsEnabled = MemoryApps.IsEnabled = MemoryAppsTrim.IsEnabled = MemoryAppsRefresh.IsEnabled = !busy;
        MemoryCleanButton.IsEnabled = RefreshButton.IsEnabled = ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _preview is { Truncated: false, Entries.Count: > 0 };
    }
    private void MemoryAutoSave_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Current; var before = settings.MemoryAutoCleanup;
        settings.MemoryAutoCleanup = new MemoryAutoCleanupOptions(MemoryAutoEnabled.IsChecked == true,
            int.Parse(((ComboBoxItem)MemoryThreshold.SelectedItem).Tag.ToString()!),
            int.Parse(((ComboBoxItem)MemoryCooldown.SelectedItem).Tag.ToString()!)).Normalize();
        try
        {
            SettingsService.Save(settings);
            MemoryAutoStatus.Text = Str.T(!settings.MemoryAutoCleanup.Enabled ? "Str.MemoryAutoOff"
                : !_memory.IsAdministrator ? "Str.MemoryAutoAdmin" : "Str.MemoryAutoWaiting");
        }
        catch (Exception ex) { settings.MemoryAutoCleanup = before; MemoryAutoStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
}
