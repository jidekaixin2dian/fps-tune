using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class PerAppFilterPanel : UserControl
{
    private readonly ForegroundFilterWatcher? _watcher;
    private readonly DispatcherTimer _statusTimer;
    private bool _suppress;
    private sealed record PresetChoice(string FileName, string Name, int? Vibrance);
    private sealed record VibranceChoice(int? Percent, string Label)
    { public override string ToString() => Label; }
    private sealed record RuleRow(FilterAppRule Rule, string Summary, string Details, string EnabledText, string RemoveName);

    public PerAppFilterPanel() : this(App.FilterWatcher) { }
    internal PerAppFilterPanel(ForegroundFilterWatcher? watcher)
    {
        InitializeComponent();
        _watcher = watcher;
        RulePresetCombo.ItemsSource = new[] { new PresetChoice("", Str.T("Str.FilterRuleNoIcc"), null) }
            .Concat(GameFilterLibrary.Presets.Select(p => new PresetChoice(p.FileName, p.Name, p.Vibrance))).ToList();
        RulePresetCombo.SelectedIndex = 0;
        RuleVibranceCombo.ItemsSource = new[] { new VibranceChoice(null, Str.T("Str.FilterRuleVibranceDefault")) }
            .Concat(Enumerable.Range(0, 101).Select(i => new VibranceChoice(i, i + "%"))).ToList();
        RuleVibranceCombo.SelectedIndex = 0;
        _suppress = true;
        WatcherCheck.IsChecked = SettingsService.Current.FilterWatcherEnabled;
        _suppress = false;
        RefreshRules(); UpdateWatcherStatus();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateWatcherStatus();
        Loaded += (_, _) => { UpdateWatcherStatus(); _statusTimer.Start(); };
        Unloaded += (_, _) => _statusTimer.Stop();
    }

    private void Watcher_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || WatcherCheck.IsChecked is null) return;
        var old = SettingsService.Current.FilterWatcherEnabled;
        SettingsService.Current.FilterWatcherEnabled = WatcherCheck.IsChecked == true;
        try { SettingsService.Save(SettingsService.Current); _watcher?.ApplySettings(); }
        catch (Exception ex)
        {
            SettingsService.Current.FilterWatcherEnabled = old;
            _suppress = true; WatcherCheck.IsChecked = old; _suppress = false;
            WatcherStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
        UpdateWatcherStatus();
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        _watcher?.ApplySettings(); UpdateWatcherStatus();
    }

    private void UpdateWatcherStatus()
    {
        var status = _watcher?.Status ?? new FilterWatcherStatus(false, null, null, Str.T("Str.FilterWatcherIdle"));
        WatcherStatus.Text = string.IsNullOrWhiteSpace(status.LastError) ? status.Detail : status.LastError;
        if (WatcherStatus.Text.Length == 0)
            WatcherStatus.Text = status.Enabled ? Str.T("Str.FilterWatcherNoMatch") : Str.T("Str.FilterWatcherIdle");
        DiagnosticsText.Text = Str.T("Str.FilterRuleDiagnosticsFormat",
            status.ActivePreset ?? Str.T("Str.FilterRuleNotActive"), status.ActiveProcess ?? "—",
            status.Suppressed ? Str.T("Str.FullscreenReasonTakeover") : Str.T("Str.FilterRuleNotActive"),
            status.ScreenIncluded ? Str.T("Str.FilterRuleOn") : Str.T("Str.FilterRuleOff"),
            status.Vibrance?.ToString() ?? "—");
    }

    private void RefreshRules()
    {
        RuleList.ItemsSource = IccAppRuleStore.Rules.Select(rule => new RuleRow(rule,
            Str.T("Str.FilterRuleSummary", rule.ProcessName, rule.PresetFileName),
            Str.T("Str.FilterRuleDetails", rule.MatchKind == FilterAppMatchKind.Path ? rule.ExePath : rule.PresetFileName,
                rule.Vibrance?.ToString() ?? Str.T("Str.FilterRuleVibranceDefault"), rule.IncludeScreen ? Str.T("Str.FilterRuleOn") : Str.T("Str.FilterRuleOff"))
            + (rule.TitleContains.Length > 0 ? " · " + rule.TitleContains : ""),
            rule.Enabled ? Str.T("Str.FilterRuleOn") : Str.T("Str.FilterRuleOff"),
            Str.T("Str.FilterRuleRemoveName", rule.ProcessName))).ToList();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RulePresetCombo.SelectedItem is not PresetChoice preset) { RuleStatus.Text = Str.T("Str.FilterPickFirst"); return; }
            var target = RuleProcessBox.Text.Trim();
            var fullPath = IccAppRuleStore.NormalizePath(target);
            if (fullPath.Length == 0 && IccAppRuleStore.NormalizeProcess(target).Length == 0)
            { RuleStatus.Text = Str.T("Str.FilterRuleNeedProcess"); return; }
            var vibrance = (RuleVibranceCombo.SelectedItem as VibranceChoice)?.Percent ?? preset.Vibrance;
            if (preset.FileName.Length == 0 && vibrance is null && RuleScreenCheck.IsChecked != true)
            { RuleStatus.Text = Str.T("Str.FilterRuleNeedPreset"); return; }
            var rule = new FilterAppRule(fullPath.Length > 0 ? Path.GetFileName(fullPath) : target,
                preset.FileName, vibrance, RuleScreenCheck.IsChecked == true, fullPath,
                RuleTitleBox.Text.Trim(), RuleEnabledCheck.IsChecked == true,
                fullPath.Length > 0 ? FilterAppMatchKind.Path : FilterAppMatchKind.ProcessName);
            var rules = IccAppRuleStore.Rules.Where(r => r.Key != rule.Key).ToList(); rules.Add(rule);
            IccAppRuleStore.Save(rules); FilterPreferenceStore.NoteUsed(preset.FileName);
            RuleProcessBox.Text = RuleTitleBox.Text = "";
            RefreshRules(); _watcher?.ApplySettings();
            RuleStatus.Text = Str.T("Str.FilterRuleSaved", rule.ProcessName);
        }
        catch (Exception ex) { RuleStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private void BrowseRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "EXE|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() == true) RuleProcessBox.Text = dialog.FileName;
    }

    private void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FilterAppRule rule }) return;
        IccAppRuleStore.Save(IccAppRuleStore.Rules.Select(r => r.Key == rule.Key ? r with { Enabled = !r.Enabled } : r).ToList());
        RefreshRules(); _watcher?.ApplySettings();
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FilterAppRule rule }) return;
        IccAppRuleStore.Remove(rule.Key); RefreshRules(); _watcher?.ApplySettings();
    }

    private void RuleVibrance_Changed(object sender, SelectionChangedEventArgs e) { }

    private void ExportRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.json", DefaultExt = ".json", FileName = "FpsTune-app-filters.json" };
            if (dialog.ShowDialog() != true) return;
            IccAppRuleStore.Export(dialog.FileName); RuleStatus.Text = Str.T("Str.FilterRuleTransferDone", IccAppRuleStore.Rules.Count);
        }
        catch (Exception ex) { RuleStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private void ImportRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            var rules = IccAppRuleStore.Import(dialog.FileName);
            RefreshRules(); _watcher?.ApplySettings(); RuleStatus.Text = Str.T("Str.FilterRuleTransferDone", rules.Count);
        }
        catch (Exception ex) { RuleStatus.Text = Str.T("Str.FilterRuleTransferFailed", PrivacyScrub.Sanitize(ex.Message)); }
    }
}
