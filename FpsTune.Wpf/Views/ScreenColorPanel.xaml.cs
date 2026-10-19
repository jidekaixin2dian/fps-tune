using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ScreenColorPanel : UserControl
{
    private string? _identity;
    private bool _syncing, _dirty, _busy;
    private int _generation;
    private readonly List<Slider> _sliders = [];
    public event Action<bool>? BusyChanged;
    private sealed record Choice(string Label, IccFilterPreset? Preset) { public override string ToString() => Label; }
    public ScreenColorPanel()
    {
        InitializeComponent();
        var names = new[] { Str.T("Str.ColorBrightness"), Str.T("Str.ColorContrast"), "Gamma", Str.T("Str.ColorShadows"), Str.T("Str.ColorRed"), Str.T("Str.ColorGreen"), Str.T("Str.ColorBlue") };
        var ranges = new (double Min, double Max, double Value)[] { (-20,20,0), (80,120,100), (.7,1.5,1), (0,25,0), (80,110,100), (80,110,100), (80,110,100) };
        for (var i = 0; i < names.Length; i++)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 24, 16) };
            var text = new TextBlock(); text.SetResourceReference(StyleProperty, "MutedTextStyle");
            var range = ranges[i]; var label = names[i];
            var slider = new Slider { Minimum = range.Min, Maximum = range.Max, Value = range.Value,
                TickFrequency = i == 2 ? .01 : 1, IsSnapToTickEnabled = true, Margin = new Thickness(0,8,0,0) };
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            text.Text = label + "  " + range.Value.ToString("0.##");
            slider.ValueChanged += (_, _) => { text.Text = label + "  " + slider.Value.ToString("0.##"); if (!_syncing) { _dirty = true; ColorPresetCombo.SelectedIndex = 0; } };
            row.Children.Add(text); row.Children.Add(slider); ParameterGrid.Children.Add(row); _sliders.Add(slider);
        }
        ColorPresetCombo.ItemsSource = new Choice[] { new(Str.T("Str.ColorCustom"), null), new(Str.T("Str.ColorVivid"), IccFilterPreset.Vivid), new(Str.T("Str.ColorShadowPreset"), IccFilterPreset.ShadowBoost),
            new(Str.T("Str.ColorDehaze"), IccFilterPreset.Dehaze), new(Str.T("Str.ColorNight"), IccFilterPreset.NightGuard), new(Str.T("Str.ColorWarm"), IccFilterPreset.Warm), new(Str.T("Str.ColorCool"), IccFilterPreset.Cool),
            new(Str.T("Str.ColorSoft"), IccFilterPreset.Soft), new(Str.T("Str.ColorGentleShadow"), IccFilterPreset.ShadowGentle), new(Str.T("Str.ColorGentleContrast"), IccFilterPreset.ContrastGentle), new(Str.T("Str.ColorMuted"), IccFilterPreset.Muted), new(Str.T("Str.ColorNeutral"), IccFilterPreset.Neutral) };
        ColorPresetCombo.SelectedIndex = 0;
        ColorApplyButton.IsEnabled = ColorRestoreButton.IsEnabled = false;
    }
    private ScreenColorSettings Values() => new(_sliders[0].Value, _sliders[1].Value, _sliders[2].Value, _sliders[3].Value,
        _sliders[4].Value, _sliders[5].Value, _sliders[6].Value);
    private void ShowSettings(ScreenColorSettings s)
    {
        _syncing = true;
        try
        {
            var values = new[] { s.Brightness, s.Contrast, s.Gamma, s.Shadows, s.Red, s.Green, s.Blue };
            for (var i = 0; i < values.Length; i++) _sliders[i].Value = values[i];
        }
        finally { _syncing = false; }
    }
    public async Task RefreshFor(string identity)
    {
        if (_busy) return;
        var gen = ++_generation;
        if (_identity != identity) { _identity = identity; _dirty = false; ColorPresetCombo.SelectedIndex = 0; }
        ColorApplyButton.IsEnabled = ColorRestoreButton.IsEnabled = false;
        var s = await Task.Run(() => ScreenColorService.GetState(identity));
        if (gen != _generation || _identity != identity || _busy) return;
        ColorStatus.Text = s.Detail;
        if (!_dirty) ShowSettings(s.Settings);
        ColorApplyButton.IsEnabled = ParameterGrid.IsEnabled = ColorPresetCombo.IsEnabled = s.Supported;
        ColorRestoreButton.IsEnabled = s.Supported && s.Restorable;
        IsEnabled = true;
    }
    public void PauseForTargetChange() { ++_generation; _identity = null; _dirty = false; IsEnabled = false; }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ColorPresetCombo.SelectedItem is not Choice { Preset: { } preset }) return;
        ShowSettings(ScreenColorSettings.ForPreset(preset)); _dirty = true;
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { ShowSettings(new()); ColorPresetCombo.SelectedIndex = 0; _dirty = true; }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null) return;
        var settings = Values(); var identity = _identity;
        if (!DialogService.Confirm(Str.T("Str.ColorApplyTitle"), Str.T("Str.ColorConfirm"), confirmText: Str.T("Str.ColorApply"))) return;
        await Run(() => {
            var rebased = ScreenColorService.Apply(settings, identity, startNewBaselineOnDrift: true);
            return Str.T("Str.ColorApplied") + (rebased ? " " + Str.T("Str.ColorNewBaselineSaved") : "");
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null) return;
        var identity = _identity;
        await Run(() => ScreenColorService.Restore(identity) ? Str.T("Str.ColorRestored") : Str.T("Str.ColorNoBackup"));
    }
    private async Task Run(Func<string> action)
    {
        _busy = true; ++_generation; IsEnabled = false; BusyChanged?.Invoke(true);
        string result;
        try { result = await Task.Run(action); _dirty = false; }
        catch (Exception ex) { result = ex.Message; DialogService.Warning(Str.T("Str.ColorIncomplete"), result); }
        finally { _busy = false; IsEnabled = true; BusyChanged?.Invoke(false); }
        if (_identity is not null) await RefreshFor(_identity);
        ColorStatus.Text = result;
    }
}
