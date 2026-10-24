using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Views;

public partial class ResolutionPanel : UserControl
{
    private sealed record TargetChoice(string Identity, string Label) { public override string ToString() => Label; }
    private sealed record ScaleChoice(uint? Value, string Label) { public override string ToString() => Label; }
    private sealed record PresetChoice(int Width, int Height, string Title, string Detail, bool Enabled, bool Selected)
    {
        public string Summary => Enabled ? Detail : Str.T("Str.ResolutionUnavailable");
    }
    private ResolutionSnapshot? _state;
    private ResolutionPlan? _plan;
    private string? _identity, _token;
    private bool _syncing = true, _busy, _trial;
    private int _generation, _presetGeneration;
    private readonly DispatcherTimer _draft = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public ResolutionPanel()
    {
        InitializeComponent();
        _draft.Tick += async (_, _) => { _draft.Stop(); await CheckDraft(); };
        _countdown.Tick += async (_, _) =>
        {
            if (_identity is null || _busy) return;
            try
            {
                var deadline = await Task.Run(() => ResolutionService.Deadline(_identity));
                if (deadline is null)
                {
                    _countdown.Stop(); _trial = false; Confirmation.Visibility = Visibility.Collapsed;
                    ResultText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.1"); await RefreshAsync(); return;
                }
                var seconds = Math.Max(0, (int)Math.Ceiling((deadline.Value - DateTimeOffset.UtcNow).TotalSeconds));
                CountdownText.Text = seconds > 0 ? Str.T("Str.Resolution.ResolutionPanel.xaml.2", seconds) : Str.T("Str.Resolution.ResolutionPanel.xaml.3");
                KeepButton.IsEnabled = seconds > 0 && _token is not null;
            }
            catch (Exception ex) { ResultText.Text = ex.Message; }
        };
        Loaded += async (_, _) => { SystemEvents.DisplaySettingsChanged += DisplaysChanged; await RefreshAsync(); };
        Unloaded += (_, _) => { SystemEvents.DisplaySettingsChanged -= DisplaysChanged; _draft.Stop(); };
        IsVisibleChanged += async (_, _) => { if (IsVisible && IsLoaded && !_busy && !_trial) await RefreshAsync(); };
        _syncing = false;
    }
    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(async () =>
    {
        if (!_busy && !_trial) await RefreshAsync();
    }));
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _busy || _trial || Target.SelectedItem is not TargetChoice target) return;
        _identity = target.Identity; ResultText.Text = ""; await RefreshAsync();
    }
    internal async Task RefreshAsync()
    {
        if (_busy || _trial) return;
        var generation = ++_generation; ApplyButton.IsEnabled = false;
        try
        {
            var displays = await Task.Run(DisplayTopology.GetDisplays);
            if (generation != _generation) return;
            _syncing = true;
            var choices = displays.Select(d => new TargetChoice(DisplayTopology.Identity(d),
                $"{d.FriendlyName} · {d.GdiDeviceName}" + (d.IsPrimary ? Str.T("Str.Resolution.ResolutionPanel.xaml.4") : ""))).ToArray();
            Target.ItemsSource = choices;
            if (_identity is null) _identity = choices.FirstOrDefault()?.Identity;
            Target.SelectedItem = choices.SingleOrDefault(c => c.Identity == _identity);
            _syncing = false;
            if (_identity is null) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionPanel.xaml.5"));
            var id = _identity;
            var state = await Task.Run(() => ResolutionService.Read(id));
            if (generation != _generation || id != _identity) return;
            _state = state; _syncing = true;
            CurrentText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.6") + state.Current;
            WidthInput.Text = state.Current.Width.ToString(); HeightInput.Text = state.Current.Height.ToString();
            LockRatio.IsChecked = state.Current.Width * 3 == state.Current.Height * 4;
            Scaling.ItemsSource = new[] { new ScaleChoice(null, Str.T("Str.Resolution.ResolutionPanel.xaml.7")), new ScaleChoice(2, Str.T("Str.Resolution.ResolutionPanel.xaml.8")), new ScaleChoice(5, Str.T("Str.Resolution.ResolutionPanel.xaml.9")), new ScaleChoice(3, Str.T("Str.Resolution.ResolutionPanel.xaml.10")) };
            Scaling.SelectedIndex = 0; Scaling.IsEnabled = state.Scaling is not null;
            ScalingText.Text = state.ScalingDetail;
            UpdateRates();
            _syncing = false;
            Inputs.IsEnabled = !state.Pending;
            RestoreButton.IsEnabled = state.Restorable;
            Target.IsEnabled = RefreshButton.IsEnabled = !state.Pending;
            if (state.Pending)
            {
                _trial = true; Confirmation.Visibility = Visibility.Visible; _countdown.Start();
                ApplyButton.IsEnabled = false; return;
            }
            await LoadPresets(state, ++_presetGeneration);
            await CheckDraft();
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            _state = null; Inputs.IsEnabled = false; ApplyButton.IsEnabled = RestoreButton.IsEnabled = false;
            CurrentText.Text = ex.Message; PlanText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.11");
        }
        finally { _syncing = false; }
    }
    private async Task LoadPresets(ResolutionSnapshot state, int generation)
    {
        var id = DisplayTopology.Identity(state.Display);
        var hz = RefreshRate.SelectedItem as int? ?? state.Current.Hz;
        var presets = await Task.Run(() => new[] { (1024, 768), (1280, 960), (1440, 1080), (1920, 1440) }.Select(size =>
        {
            try
            {
                var plan = ResolutionService.Plan(id, size.Item1, size.Item2, hz, null);
                return new PresetChoice(size.Item1, size.Item2, $"{size.Item1} × {size.Item2}",
                    $"{hz} Hz · " + (plan.CreateCustom ? Str.T("Str.Resolution.ResolutionPanel.xaml.12") : Str.T("Str.Resolution.ResolutionPanel.xaml.13")), true,
                    false);
            }
            catch (Exception ex) { return new PresetChoice(size.Item1, size.Item2, $"{size.Item1} × {size.Item2}", Str.T("Str.Resolution.ResolutionPanel.xaml.14", hz, ex.Message), false, false); }
        }).ToArray());
        if (generation == _presetGeneration)
        {
            _syncing = true; Presets.ItemsSource = presets; _syncing = false; SyncPresetSelection();
        }
    }
    private void SyncPresetSelection()
    {
        _ = int.TryParse(WidthInput.Text, out var width); _ = int.TryParse(HeightInput.Text, out var height);
        var choices = Presets.Items.Cast<PresetChoice>().ToArray();
        if (choices.All(p => p.Selected == (p.Width == width && p.Height == height))) return;
        _syncing = true;
        Presets.ItemsSource = choices.Select(p => p with { Selected = p.Width == width && p.Height == height }).ToArray();
        _syncing = false;
    }
    private void UpdateRates()
    {
        if (_state is null) return;
        var previous = RefreshRate.SelectedItem as int? ?? _state.Current.Hz;
        _ = int.TryParse(WidthInput.Text, out var width); _ = int.TryParse(HeightInput.Text, out var height);
        RefreshRate.ItemsSource = _state.Modes.Where(m => m.Width == width && m.Height == height).Select(m => m.Hz)
            .Append(_state.Current.Hz).Append(previous).Distinct().OrderByDescending(h => h).ToArray();
        RefreshRate.SelectedItem = previous;
    }
    private void PresetChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _busy || _trial || sender is not RadioButton { Tag: PresetChoice p }) return;
        _syncing = true; LockRatio.IsChecked = true;
        WidthInput.Text = p.Width.ToString(); HeightInput.Text = p.Height.ToString(); UpdateRates(); _syncing = false; QueueDraft();
    }
    private void DimensionChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || HeightInput is null || _busy || _trial) return;
        _syncing = true;
        if (LockRatio.IsChecked == true && sender is TextBox box && int.TryParse(box.Text, out var value) && value is > 0 and <= 16384)
        {
            if (box == WidthInput) HeightInput.Text = ((int)Math.Round(value * .75)).ToString();
            else WidthInput.Text = ((int)Math.Round(value * 4.0 / 3)).ToString();
        }
        UpdateRates(); _syncing = false; QueueDraft();
    }
    private void DimensionLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncing || _busy || _trial || LockRatio.IsChecked != true || sender is not TextBox box || !int.TryParse(box.Text, out var value) || value is < 200 or > 16384) return;
        _syncing = true;
        var unit = box == WidthInput ? (int)Math.Round(value / 4.0) : (int)Math.Round(value / 3.0);
        WidthInput.Text = (unit * 4).ToString(); HeightInput.Text = (unit * 3).ToString(); UpdateRates(); _syncing = false; QueueDraft();
    }
    private void RatioChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || WidthInput is null) return;
        DimensionLostFocus(WidthInput, new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, WidthInput, HeightInput));
        QueueDraft();
    }
    private void DraftChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        QueueDraft();
        if (sender == RefreshRate && _state is not null) _ = LoadPresets(_state, ++_presetGeneration);
    }
    private void QueueDraft()
    {
        _plan = null; ++_generation; ApplyButton.IsEnabled = false;
        _draft.Stop(); _draft.Start();
    }
    private async Task CheckDraft()
    {
        if (_busy || _trial || _state is null || _identity is null) return;
        var generation = ++_generation; _plan = null; ApplyButton.IsEnabled = false;
        try
        {
            if (!int.TryParse(WidthInput.Text, out var width) || !int.TryParse(HeightInput.Text, out var height) || RefreshRate.SelectedItem is not int hz)
                throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionPanel.xaml.15"));
            PendingText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.16", width, height, hz);
            SyncPresetSelection();
            if (LockRatio.IsChecked == true && width * 3L != height * 4L) throw new InvalidOperationException(Str.T("Str.Resolution.ResolutionPanel.xaml.17"));
            var id = _identity; var scale = (Scaling.SelectedItem as ScaleChoice)?.Value;
            var plan = await Task.Run(() => ResolutionService.Plan(id, width, height, hz, scale));
            if (generation != _generation) return;
            _plan = plan; PlanText.Text = plan.Detail;
            ApplyButton.Content = plan.CreateCustom ? Str.T("Str.Resolution.ResolutionPanel.xaml.18") : Str.T("Str.Resolution.ResolutionPanel.xaml.19");
            ApplyButton.IsEnabled = true;
        }
        catch (Exception ex) { if (generation == _generation) PlanText.Text = ex.Message; }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; Inputs.IsEnabled = Target.IsEnabled = RefreshButton.IsEnabled = !busy && !_trial;
        ApplyButton.IsEnabled = !busy && !_trial && _plan is not null;
        KeepButton.IsEnabled = RevertButton.IsEnabled = RestoreButton.IsEnabled = !busy;
    }
    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _trial || _plan is null || _identity is null) return;
        var id = _identity; var plan = _plan; SetBusy(true); ResultText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.20");
        try
        {
            _token = await Task.Run(() => ResolutionService.Begin(id, plan));
            _trial = true; Confirmation.Visibility = Visibility.Visible; _countdown.Start();
            var deadline = ResolutionService.Deadline(id);
            CountdownText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.2", Math.Max(0, (int)Math.Ceiling(((deadline ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds)));
            var actual = await Task.Run(() => ResolutionService.Read(id));
            ResultText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.21") + actual.Current;
            CurrentText.Text = ResultText.Text;
            ScalingText.Text = actual.ScalingDetail;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Confirmation.BringIntoView()));
        }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { SetBusy(false); if (!_trial) await RefreshAsync(); }
    }
    private async void KeepClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null || _token is null) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => ResolutionService.Confirm(_identity, _token));
            _trial = false; _countdown.Stop(); Confirmation.Visibility = Visibility.Collapsed;
            ResultText.Text = Str.T("Str.Resolution.ResolutionPanel.xaml.22");
        }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { SetBusy(false); if (!_trial) await RefreshAsync(); }
    }
    private async void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null) return;
        SetBusy(true);
        try
        {
            var restored = await Task.Run(() => ResolutionService.Restore(_identity));
            _trial = false; _token = null; _countdown.Stop(); Confirmation.Visibility = Visibility.Collapsed;
            ResultText.Text = restored ? Str.T("Str.Resolution.ResolutionPanel.xaml.23") : Str.T("Str.Resolution.ResolutionPanel.xaml.24");
        }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { SetBusy(false); if (!_trial) await RefreshAsync(); }
    }
}
