using System.Windows;
using System.Windows.Controls;
using System.IO;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ScreenColorPanel : UserControl
{
    private string? _identity;
    private bool _syncing, _dirty, _busy;
    private int _generation;
    private int _previewGeneration;
    private bool _screenSupported, _iccSupported, _restorable, _vibranceRestorable;
    private int? _vibrance;
    private bool _vibranceDirty;
    private string? _iccFile;
    private string? _lastOutcome;
    internal bool Unified { get; set; }
    public object VibranceContent { get => VibranceHost.Content; set => VibranceHost.Content = value; }
    public object IccContent { get => IccHost.Content; set => IccHost.Content = value; }
    private bool IccMode => ColorModeCombo?.SelectedIndex == 1;
    private IccFilterPreset? Preset => (IccPresetCombo.SelectedItem as Choice)?.Preset;
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
            var row = new StackPanel { Margin = new Thickness(0, 0, 16, 16) };
            var text = new TextBlock(); text.SetResourceReference(StyleProperty, "MutedTextStyle");
            var range = ranges[i]; var label = names[i];
            var slider = new Slider { Minimum = range.Min, Maximum = range.Max, Value = range.Value,
                TickFrequency = i == 2 ? .01 : 1, IsSnapToTickEnabled = true, Margin = new Thickness(0,8,0,0) };
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            text.Text = label + "  " + range.Value.ToString("0.##");
            slider.ValueChanged += (_, _) => { text.Text = label + "  " + slider.Value.ToString("0.##"); if (!_syncing) { _dirty = true; ColorPresetCombo.SelectedIndex = 0; UpdatePreview(); } };
            row.Children.Add(text); row.Children.Add(slider); ParameterGrid.Children.Add(row); _sliders.Add(slider);
        }
        ColorPresetCombo.ItemsSource = new Choice[] { new(Str.T("Str.ColorCustom"), null), new(Str.T("Str.ColorVivid"), IccFilterPreset.Vivid), new(Str.T("Str.ColorShadowPreset"), IccFilterPreset.ShadowBoost),
            new(Str.T("Str.ColorDehaze"), IccFilterPreset.Dehaze), new(Str.T("Str.ColorNight"), IccFilterPreset.NightGuard), new(Str.T("Str.ColorWarm"), IccFilterPreset.Warm), new(Str.T("Str.ColorCool"), IccFilterPreset.Cool),
            new(Str.T("Str.ColorSoft"), IccFilterPreset.Soft), new(Str.T("Str.ColorGentleShadow"), IccFilterPreset.ShadowGentle), new(Str.T("Str.ColorGentleContrast"), IccFilterPreset.ContrastGentle), new(Str.T("Str.ColorMuted"), IccFilterPreset.Muted), new(Str.T("Str.ColorNeutral"), IccFilterPreset.Neutral) };
        IccPresetCombo.ItemsSource = ((Choice[])ColorPresetCombo.ItemsSource).Select((choice, index) => index == 0 ? new Choice(Str.T("Str.ColorIccFile"), null) : choice).ToArray();
        _syncing = true;
        ColorPresetCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 1;
        _syncing = false;
        ColorApplyButton.IsEnabled = ColorRestoreButton.IsEnabled = false;
        UpdatePreview();
    }
    private async void UpdatePreview()
    {
        if (_sliders.Count != 7) return;
        ColorPreview.SetSettings(Values());
        var generation = ++_previewGeneration;
        try
        {
            string? profile = null;
            if (IccMode)
            {
                profile = _iccFile;
                if (profile is null && Preset is { } preset)
                    profile = await Task.Run(() =>
                    {
                        return ScreenColorPreview.CacheIccProfile(IccProfileGenerator.Build(preset));
                    });
            }
            if (generation == _previewGeneration) await ColorPreview.SetIccProfileAsync(profile);
        }
        catch (Exception ex) { if (generation == _previewGeneration) ColorStatus.Text = ex.Message; }
    }
    internal void SetVibrance(int? value, bool restorable = false, bool draft = false)
    { _vibrance = value; _vibranceRestorable = restorable; _vibranceDirty = draft; ColorPreview.SetVibrance(value ?? 50); Availability(); }
    internal void SelectIccFile(string path)
    {
        _syncing = true;
        try { _iccFile = path; ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = 0; }
        finally { _syncing = false; }
        _dirty = true; ColorStatus.Text = Path.GetFileName(path); Availability(); UpdatePreview();
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ColorPresetCombo is null) return;
        if (IccMode && Preset is null && _iccFile is null) { IccPresetCombo.SelectedIndex = 1; return; }
        _dirty = true; Availability(); UpdatePreview();
    }
    private void Availability()
    {
        if (ColorApplyButton is null) return;
        ColorApplyButton.IsEnabled = !_busy && (IccMode ? _iccSupported && (Preset is not null || _iccFile is not null) : _screenSupported || _vibranceDirty && _vibrance is not null);
        ParameterGrid.IsEnabled = !_busy && _screenSupported;
        ColorResetButton.IsEnabled = !_busy && _screenSupported;
        ColorPresetCombo.IsEnabled = !_busy && _screenSupported;
        IccPresetCombo.IsEnabled = !_busy && IccMode && _iccSupported;
        IccFileText.Text = _iccFile is null ? "" : Path.GetFileName(_iccFile);
        IccFileText.Visibility = IccMode && _iccFile is not null ? Visibility.Visible : Visibility.Collapsed;
        ColorRestoreButton.IsEnabled = !_busy && (_restorable || _vibranceRestorable);
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
        finally { _syncing = false; UpdatePreview(); }
    }
    public async Task RefreshFor(string identity)
    {
        if (_busy) return;
        var gen = ++_generation;
        if (_identity != identity)
        {
            _identity = identity; _syncing = true;
            try { _iccFile = null; _lastOutcome = null; ColorModeCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 1; ColorPresetCombo.SelectedIndex = 0; }
            finally { _syncing = false; _dirty = false; _vibranceDirty = false; }
        }
        ColorApplyButton.IsEnabled = ColorRestoreButton.IsEnabled = false;
        var states = await Task.Run(() => (Screen: ScreenColorService.GetState(identity), Icc: IccFilterService.GetState(identity)));
        var s = states.Screen;
        if (gen != _generation || _identity != identity || _busy) return;
        ColorStatus.Text = _lastOutcome ?? s.Detail;
        if (!_dirty)
        {
            _syncing = true;
            try
            {
                _iccFile = states.Icc.Restorable ? states.Icc.CurrentProfilePath : null;
                ColorModeCombo.SelectedIndex = _iccFile is null ? 0 : 1;
                if (_iccFile is not null) IccPresetCombo.SelectedIndex = 0;
            }
            finally { _syncing = false; }
            ShowSettings(s.Settings);
        }
        _screenSupported = s.Supported; _iccSupported = states.Icc.CanApply;
        _restorable = s.Restorable || states.Icc.Restorable;
        Availability();
        IsEnabled = true;
    }
    public void PauseForTargetChange() { ++_generation; _identity = null; _dirty = false; IsEnabled = false; }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _dirty = true; Availability();
        if (ColorPresetCombo.SelectedItem is Choice { Preset: { } preset }) ShowSettings(ScreenColorSettings.ForPreset(preset));
        else UpdatePreview();
    }
    private void IccPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ColorPreview is null) return;
        _iccFile = null; _dirty = true; Availability(); UpdatePreview();
    }
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 820;
        ControlsColumn.Width = wide ? new GridLength(e.NewSize.Width >= 1100 ? 420 : 360) : new GridLength(0);
        Grid.SetColumn(ControlsPane, wide ? 1 : 0); Grid.SetRow(ControlsPane, wide ? 0 : 1);
        PreviewPane.Margin = new Thickness(0, 0, wide ? 24 : 0, wide ? 0 : 20);
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { ShowSettings(new()); ColorPresetCombo.SelectedIndex = 0; _dirty = true; }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null) return;
        var settings = Values(); var identity = _identity;
        if (!DialogService.Confirm(Str.T("Str.ColorApplyTitle"), Str.T("Str.ColorWorkspaceConfirm", IccMode ? Str.T("Str.ColorIccMode") : Str.T("Str.ColorScreenMode")), confirmText: Str.T("Str.ColorApply"))) return;
        var iccMode = IccMode; var preset = Preset; var file = _iccFile; var vibrance = _vibranceDirty ? _vibrance : null;
        var applyScreen = _screenSupported;
        await Run(() => {
            if (Unified) return ColorWorkspaceService.Apply(identity, settings, iccMode, preset, file, vibrance, applyScreen);
            var rebased = ScreenColorService.Apply(settings, identity, startNewBaselineOnDrift: true);
            return Str.T("Str.ColorApplied") + (rebased ? " " + Str.T("Str.ColorNewBaselineSaved") : "");
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null) return;
        var identity = _identity;
        await Run(() => Unified ? ColorWorkspaceService.Restore(identity)
            : ScreenColorService.Restore(identity) ? Str.T("Str.ColorRestored") : Str.T("Str.ColorNoBackup"));
    }
    private async Task Run(Func<string> action)
    {
        _busy = true; ++_generation; IsEnabled = false; BusyChanged?.Invoke(true);
        string result;
        try { result = await Task.Run(action); _dirty = false; }
        catch (Exception ex) { result = ex.Message; DialogService.Warning(Str.T("Str.ColorIncomplete"), result); }
        finally { _busy = false; IsEnabled = true; }
        _lastOutcome = result; BusyChanged?.Invoke(false);
        if (_identity is not null) await RefreshFor(_identity);
        ColorStatus.Text = result;
    }
}
