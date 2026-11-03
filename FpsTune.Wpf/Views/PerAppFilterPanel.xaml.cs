using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class PerAppFilterPanel : UserControl
{
    private readonly ForegroundFilterWatcher? _watcher;
    private readonly DispatcherTimer _statusTimer;
    private bool _suppress;
    private bool _refreshing;
    private bool _scanning;
    private bool _scanned;
    private string? _lastActive;
    private string? _autoStatus;
    private IReadOnlyList<PerAppGameCollector.CollectedGame> _candidates = [];

    /// <summary>选中某个应用方案时，把它的进程名交给宿主页切换左侧实拍图。</summary>
    internal event Action<string?>? PreviewRequested;

    /// <summary>「编辑调色」：宿主页切到调色工作区，并把应用栏的目标设成这条规则。</summary>
    internal event Action<string>? EditRequested;

    private sealed record RuleRow(
        FilterAppRule Rule, string ExeName, string ExePath, string Summary,
        Visibility AutoBadgeVisibility, Visibility NoFilterBadgeVisibility, Visibility ToManualVisibility,
        Visibility ActiveBadgeVisibility, string RemoveName);

    private sealed record CandidateRow(string Name, string ExePath, bool Tracked)
    {
        public string Summary => Name;
        public string ActionText => Tracked ? Str.T("Str.FilterAppsCandidateAdded") : Str.T("Str.FilterAppsCandidateAdd");
        public bool CanAdd => !Tracked;
    }

    public PerAppFilterPanel() : this(App.FilterWatcher) { }

    internal PerAppFilterPanel(ForegroundFilterWatcher? watcher)
    {
        InitializeComponent();
        _watcher = watcher;
        _suppress = true;
        LoadSettings();
        _suppress = false;
        RefreshRules();
        RefreshAutoBlock();
        UpdateDaemonStatus();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) =>
        {
            UpdateDaemonStatus();
            var active = _watcher?.Status?.ActiveProcess;
            if (active == _lastActive) return;
            _lastActive = active;
            RefreshRules();
        };
        Loaded += (_, _) => { UpdateDaemonStatus(); _statusTimer.Start(); };
        Unloaded += (_, _) => _statusTimer.Stop();
    }

    internal void ActivateSelection()
    {
        if (RuleList.Items.Count == 0)
        {
            AddExe_Click(this, new RoutedEventArgs());
            return;
        }
        if (RuleList.SelectedItem is null) RuleList.SelectedIndex = 0;
    }

    /// <summary>把设置读进控件（构造与外部改动后都走这里，_suppress 期间不写回设置）。</summary>
    private void LoadSettings()
    {
        var settings = SettingsService.Current;
        WatcherCheck.IsChecked = settings.FilterWatcherEnabled;
        KeepAfterExitCheck.IsChecked = settings.FilterWatcherKeepAfterExit;
        NotifyCheck.IsChecked = settings.FilterApplyNotify;
        RestoreOnLeaveCheck.IsChecked = settings.FilterRestoreOnFocusLoss;
        FallbackCheck.IsChecked = settings.FilterAutoFallbackEnabled;
        var scope = PerAppAutoScopeRules.Parse(settings.FilterAutoScope);
        ScopeFullscreenRadio.IsChecked = scope == PerAppAutoScope.Fullscreen;
        ScopeGameDirRadio.IsChecked = scope == PerAppAutoScope.GameDirectory;
        ScopeAnyRadio.IsChecked = scope == PerAppAutoScope.AnyApp;
        UpdateScopeEnabled();
    }

    /// <summary>三个范围只在「没收录到的游戏也自动应用」开着时起作用，关着就跟着变灰。</summary>
    private void UpdateScopeEnabled()
    {
        var enabled = FallbackCheck.IsChecked == true;
        ScopeFullscreenRadio.IsEnabled = enabled;
        ScopeGameDirRadio.IsEnabled = enabled;
        ScopeAnyRadio.IsEnabled = enabled;
    }

    // ---------- 顶部开关 / 状态行 ----------

    private void Watcher_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || WatcherCheck.IsChecked is null) return;
        var settings = SettingsService.Current;
        var old = settings.FilterWatcherEnabled;
        settings.FilterWatcherEnabled = WatcherCheck.IsChecked == true;
        try
        {
            SettingsService.Save(settings);
            // 先点了「停止」再关一次开关、重新打开：跟着恢复跟随，不留在暂停态。
            if (settings.FilterWatcherEnabled && _watcher is { Paused: true }) _watcher.Resume();
            else _watcher?.ApplySettings();
        }
        catch (Exception ex)
        {
            settings.FilterWatcherEnabled = old;
            _suppress = true; WatcherCheck.IsChecked = old; _suppress = false;
            WatcherStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
        UpdateDaemonStatus();
    }

    /// <summary>「停止」：立刻停下并还原，开关保持开着；再点「重新启动」继续跟随。</summary>
    private void DaemonToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher is null || _watcher.Paused) { _watcher?.Resume(); UpdateDaemonStatus(); return; }
        _watcher.Pause();
        UpdateDaemonStatus();
    }

    private void UpdateDaemonStatus()
    {
        var status = _watcher?.Status ?? new FilterWatcherStatus(SettingsService.Current.FilterWatcherEnabled, null, null, Str.T("Str.FilterWatcherIdle"));
        var enabled = SettingsService.Current.FilterWatcherEnabled;
        var paused = status.Paused;
        DaemonStateText.Text = !enabled ? Str.T("Str.FilterAppsOff")
            : paused ? Str.T("Str.FilterAppsStopped")
            : Str.T("Str.FilterAppsRunning");
        DaemonStateText.Foreground = (Brush)FindResource(enabled && !paused ? "AccentBrush" : "TextMutedBrush");
        DaemonChip.Background = (Brush)FindResource(enabled && !paused ? "AccentSoftBrush" : "SurfaceBrush");
        DaemonNote.Text = Str.T("Str.FilterAppsDaemonNote",
            SettingsService.Current.FilterWatcherKeepAfterExit ? Str.T("Str.FilterAppsStaysOnExit") : Str.T("Str.FilterAppsStopsOnExit"));
        DaemonToggleButton.Content = paused ? Str.T("Str.FilterAppsRestart") : Str.T("Str.FilterAppsStop");
        DaemonToggleButton.IsEnabled = enabled;
        var detail = status.LastError is { Length: > 0 } error ? error : status.Detail;
        if (string.IsNullOrWhiteSpace(detail))
            detail = !enabled ? Str.T("Str.FilterWatcherIdle") : Str.T("Str.FilterWatcherNoMatch");
        WatcherStatus.Text = detail;
    }

    private void KeepAfterExit_Changed(object sender, RoutedEventArgs e) => SaveSetting(s => s.FilterWatcherKeepAfterExit = KeepAfterExitCheck.IsChecked == true);
    private void Notify_Changed(object sender, RoutedEventArgs e) => SaveSetting(s => s.FilterApplyNotify = NotifyCheck.IsChecked == true);
    private void RestoreOnLeave_Changed(object sender, RoutedEventArgs e) => SaveSetting(s => s.FilterRestoreOnFocusLoss = RestoreOnLeaveCheck.IsChecked == true);

    private void Fallback_Changed(object sender, RoutedEventArgs e)
    {
        UpdateScopeEnabled();
        SaveSetting(s => s.FilterAutoFallbackEnabled = FallbackCheck.IsChecked == true);
    }

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var scope = ScopeGameDirRadio.IsChecked == true ? PerAppAutoScope.GameDirectory
            : ScopeAnyRadio.IsChecked == true ? PerAppAutoScope.AnyApp : PerAppAutoScope.Fullscreen;
        SaveSetting(s => s.FilterAutoScope = PerAppAutoScopeRules.Key(scope));
    }

    /// <summary>三个复选框改的是设置：写盘失败就回滚界面，别让勾选状态和实际行为不一致。</summary>
    private void SaveSetting(Action<AppSettings> change)
    {
        if (_suppress) return;
        var settings = SettingsService.Current;
        try
        {
            change(settings);
            SettingsService.Save(settings);
            _watcher?.ApplySettings();
        }
        catch (Exception ex)
        {
            // 写盘失败：把磁盘上的真值读回来，勾选状态跟着回滚，不能停在"看着开了其实没开"。
            SettingsService.Load();
            _suppress = true;
            LoadSettings();
            _suppress = false;
            WatcherStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
        UpdateDaemonStatus();
    }

    // ---------- 扫描 / 收编 / 手动添加 ----------

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(showCandidates: true);

    private async void Recollect_Click(object sender, RoutedEventArgs e) => await ScanAsync(showCandidates: false);

    private async Task ScanAsync(bool showCandidates)
    {
        if (_scanning) return;
        _scanning = true;
        ScanButton.IsEnabled = RecollectButton.IsEnabled = AddExeButton.IsEnabled = false;
        var previous = ScanButton.Content;
        ScanButton.Content = Str.T("Str.FilterAppsScanning");
        try
        {
            var rules = IccAppRuleStore.Rules;
            var games = await Task.Run(() => PerAppGameCollector.Scan(rules, refresh: true));
            _scanned = true;
            if (showCandidates)
            {
                ShowCandidates(games);
                RuleStatus.Text = CandidateTitle.Text;
            }
            else
            {
                var added = PerAppGameCollector.Collect(games, CurrentTemplate());
                RefreshRules();
                _autoStatus = added > 0 ? Str.T("Str.FilterAppsCollected", added) : Str.T("Str.FilterAppsNothingNew");
                RefreshAutoBlock();
                // 收编完把候选列表的状态一起刷新，否则它还写着「还没收录」，点添加只会提示已在列表里。
                if (CandidateBox.Visibility == Visibility.Visible)
                    ShowCandidates(PerAppGameCollector.Scan(IccAppRuleStore.Rules));
                RuleStatus.Text = _autoStatus;
                _watcher?.ApplySettings();
            }
        }
        catch (Exception ex) { RuleStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally
        {
            _scanning = false;
            ScanButton.IsEnabled = RecollectButton.IsEnabled = AddExeButton.IsEnabled = true;
            ScanButton.Content = _scanned ? Str.T("Str.FilterAppsRescan") : previous;
        }
    }

    private void ShowCandidates(IReadOnlyList<PerAppGameCollector.CollectedGame> games)
    {
        _candidates = games;
        CandidateList.ItemsSource = games.Select(game => new CandidateRow(game.Name, game.ExePath, game.Tracked)).ToList();
        CandidateTitle.Text = Str.T("Str.FilterAppsScanResult", games.Count, games.Count(game => !game.Tracked));
        CandidateBox.Visibility = games.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CandidateAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CandidateRow row } || row.Tracked) return;
        try
        {
            var added = PerAppGameCollector.Collect(
                [new PerAppGameCollector.CollectedGame(row.Name, row.ExePath, false)], CurrentTemplate());
            RefreshRules();
            RefreshAutoBlock();
            RuleStatus.Text = Str.T("Str.FilterAppsCollected", added);
            ShowCandidates(_candidates.Select(game => game.ExePath.Equals(row.ExePath, StringComparison.OrdinalIgnoreCase)
                ? game with { Tracked = true } : game).ToList());
            _watcher?.ApplySettings();
        }
        catch (Exception ex) { RuleStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private void AddExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "EXE|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var path = IccAppRuleStore.DisplayPath(dialog.FileName);
            if (path.Length == 0) { RuleStatus.Text = Str.T("Str.FilterRuleNeedProcess"); return; }
            if (IccAppRuleStore.Rules.Any(r => r.Key == new FilterAppRule(Path.GetFileName(path), "", null, false, path,
                    "", true, FilterAppMatchKind.Path).Key))
            { RuleStatus.Text = Str.T("Str.FilterAppsAlreadyAdded", Path.GetFileName(path)); return; }
            if (IccAppRuleStore.Rules.Count >= IccAppRuleStore.MaxRules) { RuleStatus.Text = Str.T("Str.FilterRuleLimit", IccAppRuleStore.MaxRules); return; }
            // 手动添加只管「进这个程序时套滤镜」，滤镜本身在应用栏里选（编辑调色）。
            var rule = new FilterAppRule(Path.GetFileName(path), "", null, false, path, "", true, FilterAppMatchKind.Path);
            IccAppRuleStore.Save([.. IccAppRuleStore.Rules, rule]);
            RefreshRules();
            RuleList.SelectedItem = RuleList.Items.OfType<RuleRow>().FirstOrDefault(r => r.Rule.Key == rule.Key);
            RuleStatus.Text = Str.T("Str.FilterAppsAdded", rule.ProcessName);
        }
        catch (Exception ex) { RuleStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private static FilterAutoTemplate CurrentTemplate()
        => FilterSchemeStore.AutoTemplate ?? new FilterAutoTemplate("", null, false, null);

    // ---------- 规则列表 ----------

    private void RefreshRules()
    {
        var status = _watcher?.Status;
        var selectedKey = (RuleList.SelectedItem as RuleRow)?.Rule.Key;
        var rows = IccAppRuleStore.Rules.Select(rule => ToRow(rule, status)).ToList();
        _refreshing = true;
        try
        {
            RuleList.ItemsSource = rows;
            if (selectedKey is not null) RuleList.SelectedItem = rows.FirstOrDefault(r => r.Rule.Key == selectedKey);
        }
        finally { _refreshing = false; }
        EmptyRulesText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static RuleRow ToRow(FilterAppRule rule, FilterWatcherStatus? status)
    {
        var active = status?.ActiveProcess is { } process
                     && process.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase);
        return new RuleRow(rule,
            rule.ExePath.Length > 0 ? Path.GetFileName(rule.ExePath) : rule.ProcessName,
            rule.ExePath.Length > 0 ? rule.ExePath : Str.T("Str.FilterAppsMatchByName"),
            BuildSummary(rule),
            rule.Auto ? Visibility.Visible : Visibility.Collapsed,
            rule.HasEffect ? Visibility.Collapsed : Visibility.Visible,
            rule.Auto ? Visibility.Visible : Visibility.Collapsed,
            active ? Visibility.Visible : Visibility.Collapsed,
            Str.T("Str.FilterRuleRemoveName", rule.ProcessName));
    }

    private static string BuildSummary(FilterAppRule rule)
    {
        var parts = new List<string> { rule.PresetFileName.Length == 0 ? Str.T("Str.FilterAppsFollowDesktop") : PresetName(rule.PresetFileName) };
        if (rule.Vibrance is { } vibrance) parts.Add(Str.T("Str.FilterAppsSummaryVibrance", vibrance));
        if (rule.IncludeScreen) parts.Add(Str.T("Str.FilterAppsSummaryScreen"));
        if (rule.Screen is not null) parts.Add(Str.T("Str.FilterAppsSummarySnapshot"));
        return string.Join(" · ", parts);
    }

    private static string PresetName(string fileName)
    {
        if (fileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal)
            && Enum.TryParse<IccFilterPreset>(fileName[GameFilterLibrary.GeneratedPrefix.Length..], ignoreCase: false, out var preset))
            return ScreenColorSettings.TitleFor(preset);
        return GameFilterCatalog.All.FirstOrDefault(p => p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))?.Name
               ?? Path.GetFileNameWithoutExtension(fileName);
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || RuleList.SelectedItem is not RuleRow row) return;
        PreviewRequested?.Invoke(row.Rule.ProcessName);
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FilterAppRule rule }) return;
        EditRequested?.Invoke(rule.Key);
    }

    /// <summary>转为手动：脱离全自动模板，从此这条规则自己说了算。</summary>
    private void ToManual_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FilterAppRule rule } || !rule.Auto) return;
        IccAppRuleStore.Save(IccAppRuleStore.Rules.Select(r => r.Key == rule.Key ? r with { Auto = false } : r).ToList());
        RefreshRules(); RefreshAutoBlock(); _watcher?.ApplySettings();
        RuleStatus.Text = Str.T("Str.FilterAppsToManualDone", rule.ProcessName);
    }

    /// <summary>重置为桌面设置：清掉这条规则自己的滤镜与参数，进入时不再单独改画面。</summary>
    private void ResetDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FilterAppRule rule }) return;
        IccAppRuleStore.Save(IccAppRuleStore.Rules.Select(r => r.Key == rule.Key
            ? r with { PresetFileName = "", Vibrance = null, IncludeScreen = false, Screen = null }
            : r).ToList());
        RefreshRules(); _watcher?.ApplySettings();
        RuleStatus.Text = Str.T("Str.FilterAppsResetDone", rule.ProcessName);
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FilterAppRule rule }) return;
        if (!DialogService.Confirm(Str.T("Str.FilterRuleRemoveTitle"), Str.T("Str.FilterRuleRemoveConfirm", rule.ProcessName), danger: true))
            return;
        IccAppRuleStore.Remove(rule.Key);
        RefreshRules(); RefreshAutoBlock(); _watcher?.ApplySettings();
    }

    // ---------- 全自动 ----------

    private void RefreshAutoBlock()
    {
        var rules = IccAppRuleStore.Rules;
        var collected = PerAppGameCollector.CollectedCount(rules);
        AutoTitle.Text = Str.T("Str.FilterAppsAutoTitle", collected);
        var template = FilterSchemeStore.AutoTemplate;
        string? status = _autoStatus;
        if (collected > 0 && template is null)
            status = Str.T("Str.FilterAppsNeedTemplate");
        AutoStatus.Text = status ?? "";
    }

    // ---------- 导入 / 导出 ----------

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
            RefreshRules(); RefreshAutoBlock(); _watcher?.ApplySettings();
            RuleStatus.Text = Str.T("Str.FilterRuleTransferDone", rules.Count);
        }
        catch (Exception ex) { RuleStatus.Text = Str.T("Str.FilterRuleTransferFailed", PrivacyScrub.Sanitize(ex.Message)); }
    }
}
