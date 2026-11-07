using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class GameDriverSettings : UserControl
{
    private string? _gamePath;
    private bool _busy, _closed, _driverReady;
    private int _generation;
    internal bool IsBusy => _busy;
    internal void ShowTargetLabel() => GameTitle.Visibility = Visibility.Visible;
    public GameDriverSettings()
    {
        InitializeComponent(); UpdateSync();
        Loaded += OnLoaded;
        Unloaded += (_, _) => { _closed = true; ++_generation; GameContextService.GameSwitched -= ContextChanged; };
        IsVisibleChanged += async (_, _) => { if (IsVisible && IsLoaded) await SetGameAsync(AppState.GamePath); };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _closed = false; GameContextService.GameSwitched += ContextChanged;
        if (IsVisible) await SetGameAsync(AppState.GamePath);
    }
    private async void ContextChanged() { ++_generation; if (IsVisible) await SetGameAsync(AppState.GamePath); }
    private async Task SetGameAsync(string? path)
    {
        if (_busy) return;
        var changed = !string.Equals(path, _gamePath, StringComparison.OrdinalIgnoreCase);
        _gamePath = path; GameTitle.Text = path is null ? Str.T("Str.NoGameSelected") : GameContextService.DisplayNameFor(path);
        if (changed) { SyncBox.SelectedIndex = 0; VrrConfirmed.IsChecked = PowerCheck.IsChecked = ForegroundCheck.IsChecked = BackgroundCheck.IsChecked = false; ForegroundBox.Text = "240"; BackgroundBox.Text = "30"; }
        DriverStatus.Text = Str.T("Str.TuningPending");
        if (path is null || !File.Exists(path))
        { _driverReady = false; DriverInputs.IsEnabled = ApplyDriverButton.IsEnabled = RestoreDriverButton.IsEnabled = false; DriverCurrent.Text = Str.T("Str.PlanSelectGame"); return; }
        DriverInputs.IsEnabled = true; await RefreshDriverAsync();
    }
    private bool CanAct()
    {
        if (_closed || !string.Equals(_gamePath, AppState.GamePath, StringComparison.OrdinalIgnoreCase)) { DriverStatus.Text = Str.T("Str.PlanGameChanged"); return false; }
        if (_busy || GameContextService.IsBusy || GamePreparationService.IsRunning || ExperimentRunner.IsRunning || App.SessionService.IsRunning || App.SessionService.IsStopping)
        { DriverStatus.Text = Str.T("Str.TuningBusy"); return false; }
        return true;
    }
    internal async Task RefreshDriverAsync()
    {
        var path = _gamePath;
        var generation = ++_generation;
        _driverReady = false; ApplyDriverButton.IsEnabled = RestoreDriverButton.IsEnabled = false;
        _advancedRows.Clear(); AdvancedList.ItemsSource = null; AdvancedStateText.Text = "";
        if (path is null) return;
        try
        {
            var exe = Path.GetFileName(path);
            var (state, advanced) = await Task.Run(() =>
                (DisplayQualityService.GetDrsGameSettings(exe), DisplayQualityService.ReadAdvancedSettings(exe)));
            if (_closed || generation != _generation) return;
            _driverReady = true;
            var sync = state.VSync switch { VSyncMode.ForceOff => Str.T("Str.Off"), VSyncMode.ForceOn => Str.T("Str.TuningEnabled"), _ => Str.T("Str.FollowDriverGame") };
            var power = state.PowerMode switch { PowerMode.PreferMax => Str.T("Str.PowerPreferMax"), PowerMode.Adaptive => Str.T("Str.PowerAdaptive"),
                PowerMode.DriverControlled => Str.T("Str.PowerDriverControlled"), PowerMode.PreferConsistentPerformance => Str.T("Str.PowerStablePerf"),
                PowerMode.PreferMin => Str.T("Str.PowerLowestConsumption"), PowerMode.OptimalPower => Str.T("Str.PowerOptimal"), _ => Str.T("Str.FollowDriverGame") };
            DriverCurrent.Text = Str.T("Str.TuningDriverCurrent", sync, power, Cap(state.FrameRateLimit), Cap(state.BackgroundFrameRateLimit));
            RestoreDriverButton.IsEnabled = DisplayQualityService.HasCompetitiveBackup(Path.GetFileName(path));
            ApplyAdvanced(advanced);
        }
        catch (Exception ex)
        {
            if (_closed || generation != _generation) return;
            _driverReady = false; DriverCurrent.Text = Str.T("Str.TuningDriverUnavailable", PrivacyScrub.Sanitize(ex.Message));
            AdvancedStateText.Text = Str.T("Str.TuningDriverUnavailable", PrivacyScrub.Sanitize(ex.Message));
        }
        if (generation == _generation) ApplyDriverButton.IsEnabled = _driverReady && !_busy;
    }

    // ---------- 高级参数：三态 + 逐项写入 ----------

    private readonly List<AdvancedSettingRow> _advancedRows = new();

    private void ApplyAdvanced(DisplayQualityService.DrsAdvancedSnapshot snapshot)
    {
        _advancedRows.Clear();
        foreach (var item in snapshot.Items)
        {
            var definition = NvdrsSettingCatalog.Find(item.SettingId);
            if (definition is not null) _advancedRows.Add(new AdvancedSettingRow(definition, item));
        }
        AdvancedList.ItemsSource = _advancedRows;
        AdvancedStateText.Text = snapshot.ProfileFound ? Str.T("Str.AdvancedReady") : Str.T("Str.AdvancedNoProfile");
        AdvancedWriteButton.IsEnabled = snapshot.ProfileFound && !_busy;
    }

    internal IReadOnlyList<AdvancedSettingRow> AdvancedRows => _advancedRows;

    internal async Task ChangeAdvancedAsync()
    {
        if (_busy || !_driverReady || _gamePath is null || _advancedRows.Count == 0) return;
        var changes = new List<(uint SettingId, uint? Value, string Name)>();
        foreach (var row in _advancedRows)
        {
            if (!row.TryReadWriteValue(out var value, out var error)) { AdvancedStatus.Text = error ?? ""; return; }
            if (row.IsChanged(value)) changes.Add((row.SettingId, value, row.Name));
        }
        if (changes.Count == 0) { AdvancedStatus.Text = Str.T("Str.AdvancedNoChange"); return; }
        if (!DialogService.Confirm(Str.T("Str.AdvancedTitle"),
                Str.T("Str.AdvancedConfirm", string.Join(" · ", changes.Select(c => c.Name))), confirmText: Str.T("Str.Apply")))
            return;

        _busy = true; AdvancedWriteButton.IsEnabled = ApplyDriverButton.IsEnabled = false;
        AdvancedStatus.Text = Str.T("Str.Writing");
        var exe = Path.GetFileName(_gamePath);
        var applied = 0; var failures = new List<string>();
        try
        {
            foreach (var (settingId, value, _) in changes)
            {
                var receipt = await Task.Run(() => DisplayQualityService.SetAdvancedSetting(exe, settingId, value));
                applied += receipt.AppliedCount;
                failures.AddRange(receipt.Items
                    .Where(i => i.Outcome != DisplayQualityService.DrsSettingOutcome.Applied).Select(i => i.Name));
            }
            AdvancedStatus.Text = failures.Count == 0
                ? Str.T("Str.AdvancedWritten", applied)
                : Str.T("Str.AdvancedWrittenPartial", applied, failures.Count, string.Join(" · ", failures));
        }
        catch (Exception ex)
        {
            failures.Add(PrivacyScrub.Sanitize(ex.Message));
            AdvancedStatus.Text = Str.T("Str.AdvancedWrittenPartial", applied, failures.Count, string.Join(" · ", failures));
        }
        finally
        {
            _busy = false;
            await RefreshDriverAsync();
        }
    }

    private async void AdvancedWrite_Click(object sender, RoutedEventArgs e) => await ChangeAdvancedAsync();
    private static string Cap(uint? value) => value is null ? Str.T("Str.FollowDriverGame") : value == 0 ? Str.T("Str.FrameOff") : value + " FPS";
    private static uint? ReadCap(CheckBox selected, TextBox input, bool background)
    {
        if (selected.IsChecked != true) return null;
        if (!uint.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || !DisplayQualityService.IsValidFrameLimit(value, background))
            throw new ArgumentException(Str.T("Str.FrameRange"));
        return value;
    }
    internal async Task ChangeDriverAsync(bool restore)
    {
        if (!CanAct() || !_driverReady || !File.Exists(_gamePath)) return;
        DisplayQualityService.CompetitiveSettings? selection = null;
        try
        {
            if (!restore)
            {
                selection = new(SyncBox.SelectedIndex == 2, VrrConfirmed.IsChecked == true, PowerCheck.IsChecked == true,
                    ReadCap(ForegroundCheck, ForegroundBox, false), ReadCap(BackgroundCheck, BackgroundBox, true), SyncBox.SelectedIndex != 0);
                if (selection.SmoothVrr && !selection.VrrAndReflexConfirmed) throw new ArgumentException(Str.T("Str.TuningConfirmVrr"));
                if (!selection.ChangeSync && !selection.MaximumPower && selection.Foreground is null && selection.Background is null)
                    throw new ArgumentException(Str.T("Str.DriverChooseAction"));
            }
            var details = restore ? Str.T("Str.TuningRestoreConfirm") : Str.T("Str.TuningApplyConfirm", ((ComboBoxItem)SyncBox.SelectedItem).Content,
                selection!.MaximumPower ? Str.T("Str.TuningMaximumPower") : Str.T("Str.FrameKeep"),
                selection.Foreground is null ? Str.T("Str.FrameKeep") : Cap(selection.Foreground), selection.Background is null ? Str.T("Str.FrameKeep") : Cap(selection.Background));
            if (!DialogService.Confirm(Str.T("Str.OptDriverTab"), GameTitle.Text + "\n\n" + details)) return;
            if (!CanAct()) return;
            _busy = true; DriverInputs.IsEnabled = ApplyDriverButton.IsEnabled = RestoreDriverButton.IsEnabled = false;
            var exe = Path.GetFileName(_gamePath);
            var changed = await Task.Run(() => { if (restore) return DisplayQualityService.RestoreCompetitiveSettings(exe); DisplayQualityService.ApplyCompetitiveSettings(exe, selection!); return true; });
            DriverStatus.Text = Str.T(changed ? restore ? "Str.TuningDriverRestored" : "Str.TuningDriverApplied" : "Str.NothingToRestore");
        }
        catch (Exception ex) { DriverStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally
        {
            _busy = false; DriverInputs.IsEnabled = true;
            if (!_closed)
            {
                if (string.Equals(_gamePath, AppState.GamePath, StringComparison.OrdinalIgnoreCase)) await RefreshDriverAsync();
                else await SetGameAsync(AppState.GamePath);
            }
        }
    }
    private async void ApplyDriver_Click(object sender, RoutedEventArgs e) => await ChangeDriverAsync(false);
    private async void RestoreDriver_Click(object sender, RoutedEventArgs e) => await ChangeDriverAsync(true);
    private void Sync_Changed(object sender, SelectionChangedEventArgs e) { if (SyncHint is not null) { UpdateSync(); DriverInput_Changed(sender, e); } }
    private void DriverInput_Changed(object sender, RoutedEventArgs e) { if (DriverStatus is not null && !_busy) DriverStatus.Text = Str.T("Str.TuningPending"); }
    private void UpdateSync()
    {
        SyncHint.Text = Str.T(SyncBox.SelectedIndex == 2 ? "Str.TuningSmoothHint" : SyncBox.SelectedIndex == 1 ? "Str.TuningLatencyHint" : "Str.DriverKeepSyncHint");
        VrrConfirmed.Visibility = SyncBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
