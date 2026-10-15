using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class GamePreparationWindow : Window
{
    private sealed record DisplayChoice(string Identity, string Label) { public override string ToString() => Label; }
    private sealed record IccChoice(IccFilterPreset? Preset, string Label) { public override string ToString() => Label; }
    private readonly GamePreparationOptions _original;
    private bool _loaded;
    internal bool RunNow { get; private set; }
    internal bool ReviewBasics { get; private set; }

    public GamePreparationWindow()
    {
        InitializeComponent();
        _original = SettingsService.Current.Preparation;
        MemoryCheck.IsChecked = _original.CleanMemory; MemoryMode.SelectedIndex = _original.AllStandby ? 1 : 0;
        IccCheck.IsChecked = _original.ApplyIcc; VibranceCheck.IsChecked = _original.ApplyVibrance;
        VibranceSlider.Value = Math.Clamp(_original.VibrancePercent, 0, 100);
        var presets = new[] { new IccChoice(null, Str.T("Str.PrepareOriginalIcc")) }
            .Concat(Enum.GetValues<IccFilterPreset>().Select(p => new IccChoice(p, Str.T(IccProfileGenerator.ResourceKeysFor(p).Title)))).ToArray();
        IccPresetBox.ItemsSource = presets; IccPresetBox.SelectedItem = presets.FirstOrDefault(p => p.Preset == _original.IccPreset) ?? presets[0];
        var pending = DeltaPreparation.PendingItems(AppState.Items, true);
        var ready = AppState.Items.Count > 0 && string.Equals(AppState.DetectJson?["gamePath"]?.GetValue<string>(), AppState.GamePath, StringComparison.OrdinalIgnoreCase);
        BasicGuide.Text = Str.T("Str.PrepareFirstGuide") + "\n\n" + (ready ? Str.T("Str.PrepareBasicsSummary", pending.Count) : Str.T("Str.PrepareNeedsDetection"));
        Loaded += LoadDisplays;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private async void LoadDisplays(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = RunButton.IsEnabled = false;
        try
        {
            var displays = await Task.Run(DisplayTopology.GetDisplays);
            if (!IsLoaded) return;
            var choices = displays.Where(d => d.Independent).Select(d => new DisplayChoice(DisplayTopology.Identity(d),
                string.IsNullOrWhiteSpace(d.FriendlyName) ? d.GdiDeviceName : d.FriendlyName + " · " + d.GdiDeviceName)).ToArray();
            DisplayBox.ItemsSource = choices;
            DisplayBox.SelectedItem = _original.DisplayIdentity is null ? choices.FirstOrDefault() : choices.FirstOrDefault(c => c.Identity == _original.DisplayIdentity);
            DisplayBox.IsEnabled = choices.Length > 0;
            if (choices.Length == 0 || DisplayBox.SelectedItem is null) StatusText.Text = Str.T("Str.PrepareSelectDisplay");
        }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _loaded = true; SaveButton.IsEnabled = RunButton.IsEnabled = true; }
    }

    private void Save(bool run)
    {
        if (!_loaded) return;
        var selected = new GamePreparationOptions { Configured = true, CleanMemory = MemoryCheck.IsChecked == true,
            AllStandby = MemoryMode.SelectedIndex == 1, ApplyIcc = IccCheck.IsChecked == true,
            IccPreset = (IccPresetBox.SelectedItem as IccChoice)?.Preset, ApplyVibrance = VibranceCheck.IsChecked == true,
            VibrancePercent = (int)VibranceSlider.Value, DisplayIdentity = (DisplayBox.SelectedItem as DisplayChoice)?.Identity };
        if (!selected.HasActions) { StatusText.Text = Str.T("Str.PrepareChoose"); return; }
        if ((selected.ApplyIcc || selected.ApplyVibrance) && selected.DisplayIdentity is null)
        { StatusText.Text = Str.T("Str.PrepareSelectDisplay"); return; }
        try
        {
            SettingsService.Current.Preparation = selected; SettingsService.Save(SettingsService.Current);
            RunNow = run; DialogResult = true;
        }
        catch (Exception ex)
        {
            SettingsService.Current.Preparation = _original;
            DialogService.Warning(Str.T("Str.PrepareSaveFailed"), ex.Message);
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void Run_Click(object sender, RoutedEventArgs e) => Save(true);
    private void ReviewBasics_Click(object sender, RoutedEventArgs e) { ReviewBasics = true; DialogResult = false; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
