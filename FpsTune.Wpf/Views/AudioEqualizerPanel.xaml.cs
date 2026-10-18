using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class AudioEqualizerPanel : UserControl
{
    private readonly List<Slider> _bands = [];
    private EqualizerEngine? _engine;
    private bool _busy, _syncing;
    private static readonly (string Name, double[] Gains)[] Presets = [
        (Str.T("Str.EqFlat"), new double[10]), (Str.T("Str.EqFootsteps"), [-4,-3,-2,0,1,2,3,3,1,0]),
        (Str.T("Str.EqVoice"), [-2,-2,-1,0,2,3,3,1,0,-1]), (Str.T("Str.EqBass"), [5,4,3,1,0,0,0,0,0,0])];
    public AudioEqualizerPanel()
    {
        InitializeComponent();
        foreach (var hz in EqualizerSettings.Frequencies)
        {
            var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
            var title = new TextBlock { Text = hz >= 1000 ? hz / 1000 + "k" : hz.ToString(), HorizontalAlignment = HorizontalAlignment.Center };
            title.SetResourceReference(StyleProperty, "MutedTextStyle");
            var slider = new Slider { Minimum = -12, Maximum = 12, Value = 0, Orientation = Orientation.Vertical,
                Height = 160, TickFrequency = .5, IsSnapToTickEnabled = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,10,0,8) };
            slider.Style = (Style)FindResource("VerticalEqSlider");
            System.Windows.Automation.AutomationProperties.SetName(slider, hz + Str.T("Str.EqHzGain"));
            var value = new TextBlock { Text = "0 dB", HorizontalAlignment = HorizontalAlignment.Center };
            value.SetResourceReference(StyleProperty, "MutedTextStyle");
            slider.ValueChanged += (_, _) => { value.Text = slider.Value.ToString("0.#") + " dB"; UpdateHeadroom(); if (!_syncing) EqualizerPresetCombo.SelectedIndex = -1; };
            panel.Children.Add(title); panel.Children.Add(slider); panel.Children.Add(value); BandsGrid.Children.Add(panel); _bands.Add(slider);
        }
        EqualizerPresetCombo.ItemsSource = Presets.Select(p => p.Name); EqualizerPresetCombo.SelectedIndex = 0;
        Loaded += async (_, _) => await RefreshAsync();
    }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _bands.Count != 10 || EqualizerPresetCombo.SelectedIndex < 0) return;
        var gains = Presets[EqualizerPresetCombo.SelectedIndex].Gains;
        _syncing = true;
        try { for (var i = 0; i < _bands.Count; i++) _bands[i].Value = gains[i]; }
        finally { _syncing = false; }
        UpdateHeadroom();
    }
    private void Preamp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateHeadroom();
    private void UpdateHeadroom()
    {
        if (PreampText is null || _bands.Count != 10) return;
        var actual = Math.Min(PreampSlider.Value, -Math.Max(0, _bands.Max(s => s.Value)));
        PreampText.Text = Str.T("Str.EqHeadroom", actual.ToString("0.#"));
    }
    private async Task RefreshAsync()
    {
        if (_busy) return;
        try
        {
            var selected = (OutputCombo.SelectedItem as AudioOutput)?.Id;
            var snapshot = await Task.Run(() => (Engine: AudioEqualizerService.FindEngine(), Outputs: AudioEqualizerService.Outputs()));
            _engine = snapshot.Engine;
            var saved = SettingsService.Current.EqualizerDraft ?? AudioEqualizerService.SavedSettings();
            OutputCombo.ItemsSource = snapshot.Outputs;
            OutputCombo.SelectedItem = snapshot.Outputs.FirstOrDefault(d => d.Id == (selected ?? saved?.DeviceId));
            if (saved is not null && selected is null)
            {
                saved.Validate(); _syncing = true;
                try { for (var i = 0; i < 10; i++) _bands[i].Value = saved.Gains[i]; PreampSlider.Value = saved.Preamp; EnabledCheck.IsChecked = saved.Enabled; EqualizerPresetCombo.SelectedIndex = -1; }
                finally { _syncing = false; }
            }
            EngineStatus.Text = _engine is null
                ? Str.T("Str.EqFirstUse")
                : Str.T("Str.EqFound");
            DeviceSetupButton.IsEnabled = EqApplyButton.IsEnabled = _engine is not null;
            EqRestoreButton.IsEnabled = AudioEqualizerService.HasBackup;
            UpdateHeadroom();
        }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private EqualizerSettings ReadSettings()
    {
        if (OutputCombo.SelectedItem is not AudioOutput output) throw new InvalidOperationException(Str.T("Str.EqChooseOutput"));
        var settings = new EqualizerSettings(output.Id, _bands.Select(s => s.Value).ToArray(), PreampSlider.Value, EnabledCheck.IsChecked == true);
        settings.Validate(); return settings;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { SettingsService.Current.EqualizerDraft = ReadSettings(); SettingsService.Save(SettingsService.Current); EqStatus.Text = Str.T("Str.EqSaved"); }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettings(); SettingsService.Current.EqualizerDraft = settings; SettingsService.Save(SettingsService.Current);
            await Run(() => { AudioEqualizerService.Apply(settings); return settings.Enabled
                ? Str.T("Str.EqApplied")
                : Str.T("Str.EqBypassed"); });
        }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Run(() => AudioEqualizerService.Restore() ? Str.T("Str.EqRestored") : Str.T("Str.EqNoBackup"));
    private async Task Run(Func<string> work)
    {
        if (_busy) return;
        _busy = true; IsEnabled = false;
        try { EqStatus.Text = await Task.Run(work); }
        catch (UnauthorizedAccessException) { EqStatus.Text = Str.T("Str.EqPermission"); }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
        finally { _busy = false; IsEnabled = true; EqRestoreButton.IsEnabled = AudioEqualizerService.HasBackup; }
    }
    private void Install_Click(object sender, RoutedEventArgs e) => ProductLinks.Open("https://sourceforge.net/projects/equalizerapo/");
    private void DeviceSetup_Click(object sender, RoutedEventArgs e)
    {
        try { if (_engine is not null) Process.Start(new ProcessStartInfo(_engine.DeviceTool) { UseShellExecute = true }); }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}
