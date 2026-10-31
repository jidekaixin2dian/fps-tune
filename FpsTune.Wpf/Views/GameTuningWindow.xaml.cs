using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class GameTuningWindow : Window
{
    private sealed record Source(string Path, string Label) { public override string ToString() => Label; }
    private readonly string _gamePath;
    private readonly CompetitiveGame _game;
    private readonly IReadOnlyList<string>? _candidates;
    private GameVideoConfig? _config;
    private bool _closed;
    private bool _busy => DriverSettings.IsBusy;
    private int _configGeneration;
    internal SessionConditions? Recording { get; private set; }
    internal bool OpenDisplay { get; private set; }
    internal Action<string> CopyText = Clipboard.SetText;
    public GameTuningWindow(string gamePath) : this(gamePath, null) { }
    internal GameTuningWindow(string gamePath, IReadOnlyList<string>? candidates)
    {
        _gamePath = gamePath; _game = GameTuning.Identify(gamePath) ?? throw new ArgumentException(Str.T("Str.TuningUnsupported"));
        _candidates = candidates; InitializeComponent();
        GameTitle.Text = Str.T("Str.TuningGameTitle", GameContextService.DisplayNameFor(gamePath)); GameGuide.Text = GameTuning.Guide(_game);
        SceneText.Text = Str.T("Str.TuningSceneHint", GameTuning.Scene(_game));
        Cs2Tab.Visibility = _game == CompetitiveGame.Cs2 ? Visibility.Visible : Visibility.Collapsed;
        DeltaDlssCard.Visibility = _game == CompetitiveGame.DeltaForce ? Visibility.Visible : Visibility.Collapsed;
        ShowSection(); UpdateCommand();
        Loaded += async (_, _) => { await DiscoverAsync(); };
        Closed += (_, _) => { _closed = true; ++_configGeneration; };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; StatusText.Text = Str.T("Str.TuningBusy"); } };
    }
    private async Task DiscoverAsync()
    {
        try
        {
            var paths = _candidates ?? await Task.Run(() => GameVideoConfig.Discover(_gamePath, _game));
            if (_closed) return;
            SourceBox.ItemsSource = paths.Select((p, i) => new Source(p, Str.T("Str.TuningSourceNumber", i + 1, File.GetLastWriteTime(p)))).ToArray();
            ConfigStatus.Text = Str.T(paths.Count == 0 ? "Str.TuningNoConfig" : paths.Count > 1 ? "Str.TuningSelectAccount" : "Str.TuningReading");
            SourceBox.SelectedIndex = paths.Count == 1 ? 0 : -1;
        }
        catch (Exception ex) { ConfigStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private async void Source_Changed(object sender, SelectionChangedEventArgs e) => await LoadSourceAsync();
    private async Task LoadSourceAsync()
    {
        var generation = ++_configGeneration; _config = null;
        StatusText.Text = "";
        SaveBaselineButton.IsEnabled = PrepareRecordButton.IsEnabled = false; ConfigRows.ItemsSource = null;
        SourcePathText.Text = BaselineStatus.Text = "";
        if (SourceBox.SelectedItem is not Source source) return;
        try
        {
            var config = await Task.Run(() => GameVideoConfig.Read(source.Path, _game));
            if (_closed || generation != _configGeneration) return;
            _config = config; SourcePathText.Text = config.Path; SourcePathText.ToolTip = config.Path;
            ConfigStatus.Text = Str.T("Str.TuningConfigTime", config.LastWrite);
            PrepareRecordButton.IsEnabled = true; ShowComparison();
        }
        catch (Exception ex) { if (generation == _configGeneration && !_closed) ConfigStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void ShowComparison()
    {
        if (_config is not { } config) return;
        GameConfigBaseline? baseline = null; string? baselineError = null;
        try { baseline = GameConfigBaselines.Load(_gamePath, config.Path); }
        catch (Exception ex) { baselineError = PrivacyScrub.Sanitize(ex.Message); }
        var changed = 0;
        ConfigRows.ItemsSource = GameVideoConfig.Fields.Select(key =>
        {
            var difference = baseline is not null && baseline.Values.GetValueOrDefault(key) != config.Values.GetValueOrDefault(key);
            if (difference) changed++;
            return new { Label = GameVideoConfig.Label(key), Before = baseline is null ? "—" : GameVideoConfig.DisplayValue(key, baseline.Values.GetValueOrDefault(key)),
                After = config.DisplayValue(key), Changed = difference };
        }).ToArray();
        BaselineStatus.Text = baselineError ?? (baseline is null ? Str.T("Str.TuningNoBaseline") : Str.T("Str.TuningBaselineTime", baseline.SavedAt.ToLocalTime(), changed));
        SaveBaselineButton.IsEnabled = baselineError is null;
    }
    private GameVideoConfig FreshConfig()
    {
        if (_config is null) throw new InvalidOperationException(Str.T("Str.TuningSelectAccount"));
        var fresh = GameVideoConfig.Read(_config.Path, _game);
        if (fresh.Sha256 != _config.Sha256) throw new IOException(Str.T("Str.TuningConfigChanged"));
        return fresh;
    }
    private void SaveBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAct()) return;
        try { GameConfigBaselines.Save(_gamePath, FreshConfig()); ShowComparison(); StatusText.Text = Str.T("Str.TuningBaselineSaved"); }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) { if (!_busy) await LoadSourceAsync(); }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = Str.T("Str.TuningChooseFile"), Filter = _game == CompetitiveGame.Cs2 ? "CS2|cs2_video.txt" : "GameUserSettings|GameUserSettings.ini" };
        if (picker.ShowDialog(this) != true) return;
        SourceBox.ItemsSource = new[] { new Source(picker.FileName, Path.GetFileName(picker.FileName)) }; SourceBox.SelectedIndex = 0;
    }
    private void PrepareRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAct()) return;
        try
        {
            var seconds = int.Parse((string)((ComboBoxItem)DurationBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
            Recording = GameTuning.RecordingConditions(FreshConfig(), _game, seconds); DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private bool CanAct()
    {
        if (!string.Equals(_gamePath, AppState.GamePath, StringComparison.OrdinalIgnoreCase)) { StatusText.Text = Str.T("Str.PlanGameChanged"); return false; }
        if (_busy || GameContextService.IsBusy || ExperimentRunner.IsRunning || App.SessionService.IsRunning || App.SessionService.IsStopping)
        { StatusText.Text = Str.T("Str.TuningBusy"); return false; }
        return true;
    }
    internal Task ChangeDriverAsync(bool restore) => DriverSettings.ChangeDriverAsync(restore);
    private void Tab_Changed(object sender, RoutedEventArgs e) { if (ConfigPanel is not null) ShowSection(); }
    private void ShowSection()
    {
        ConfigPanel.Visibility = ConfigTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DriverPanel.Visibility = DriverTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CommandPanel.Visibility = Cs2Tab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ScrollToHome();
    }
    private void Command_Changed(object sender, TextChangedEventArgs e) { if (CommandPreview is not null) UpdateCommand(); }
    private void UpdateCommand()
    {
        try { CommandPreview.Text = GameTuning.Cs2Commands(Cs2Foreground.Text, Cs2Menu.Text); CopyCommandButton.IsEnabled = true; CommandStatus.Text = ""; }
        catch (Exception ex) { CommandPreview.Text = ""; CopyCommandButton.IsEnabled = false; CommandStatus.Text = ex.Message; }
    }
    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        try { var text = GameTuning.Cs2Commands(Cs2Foreground.Text, Cs2Menu.Text); CopyText(text); CommandStatus.Text = Str.T("Str.TuningCommandCopied"); }
        catch (Exception ex) { CommandStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void Display_Click(object sender, RoutedEventArgs e) { if (CanAct()) { OpenDisplay = true; DialogResult = true; } }
    private void Official_Click(object sender, RoutedEventArgs e)
    { try { Process.Start(new ProcessStartInfo(GameTuning.Source(_game)) { UseShellExecute = true }); } catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
