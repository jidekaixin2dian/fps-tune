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
        if (path is null) return;
        try
        {
            var state = await Task.Run(() => DisplayQualityService.GetDrsGameSettings(Path.GetFileName(path)));
            if (_closed || generation != _generation) return;
            _driverReady = true;
            var sync = state.VSync switch { VSyncMode.ForceOff => Str.T("Str.Off"), VSyncMode.ForceOn => Str.T("Str.TuningEnabled"), _ => Str.T("Str.FollowDriverGame") };
            var power = state.PowerMode switch { PowerMode.PreferMax => Str.T("Str.PowerPreferMax"), PowerMode.Adaptive => Str.T("Str.PowerAdaptive"),
                PowerMode.DriverControlled => Str.T("Str.PowerDriverControlled"), PowerMode.PreferConsistentPerformance => Str.T("Str.PowerStablePerf"),
                PowerMode.PreferMin => Str.T("Str.PowerLowestConsumption"), PowerMode.OptimalPower => Str.T("Str.PowerOptimal"), _ => Str.T("Str.FollowDriverGame") };
            DriverCurrent.Text = Str.T("Str.TuningDriverCurrent", sync, power, Cap(state.FrameRateLimit), Cap(state.BackgroundFrameRateLimit));
            RestoreDriverButton.IsEnabled = DisplayQualityService.HasCompetitiveBackup(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            if (_closed || generation != _generation) return;
            _driverReady = false; DriverCurrent.Text = Str.T("Str.TuningDriverUnavailable", PrivacyScrub.Sanitize(ex.Message));
        }
        if (generation == _generation) ApplyDriverButton.IsEnabled = _driverReady && !_busy;
    }
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
