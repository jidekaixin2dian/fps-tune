using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ConsoleView : UserControl
{
    private MetricsSampler? _sampler;
    private Window? _owner;
    private bool _refreshing;
    private bool _locating;

    // 概览对所有 FPS 游戏通用（红线四：游戏相关只做路径级适配）：
    // 基础建议三项（游戏模式/后台录制/显卡偏好）是系统层设置，不挑游戏。
    private bool GameLocated => !string.IsNullOrWhiteSpace(AppState.GamePath)
        && System.IO.File.Exists(AppState.GamePath);
    private bool GameReady => GameLocated && AppState.Items.Count > 0
        && string.Equals(AppState.DetectJson?["gamePath"]?.GetValue<string>(), AppState.GamePath, StringComparison.OrdinalIgnoreCase);

    private void UpdatePreparation()
    {
        // 指标卡"已优化"格：随检测状态刷新（原 RebuildRows 职责，优化项列表移除后并入此处）。
        if (TunedText is not null)
        {
            var tuned = AppState.Items.Where(i => ItemCatalog.All.FirstOrDefault(d => d.Id == i.Id)?.Available != false).ToArray();
            var tunedCount = tuned.Count(i => i.Optimized);
            TunedText.Text = tuned.Length > 0 ? $"{tunedCount}/{tuned.Length}" : "—";
            TunedMeter.Value = tuned.Length > 0 ? 100.0 * tunedCount / tuned.Length : 0;
        }
        TuningButton.Visibility = GameTuning.Identify(AppState.GamePath) is not null ? Visibility.Visible : Visibility.Collapsed;
        TuningButton.IsEnabled = !_preparing && !_refreshing && !_locating;
        var count = DeltaPreparation.PendingItems(AppState.Items, GameReady).Count;
        var firstRun = !StateStore.HasSeenOnboarding;
        GameReadyTitle.Text = GameLocated
            ? Str.T("Str.ConsoleReadyTitleFmt", GameContextService.DisplayNameFor(AppState.GamePath!))
            : Str.T("Str.ConsoleReadyTitle");
        GameReadyText.Text = _locating ? Str.T("Str.LocatingAndDetecting") : !GameReady
            ? GameLocated ? Str.T("Str.GameSelectedRefreshDetect") : Str.T("Str.LocateGameForAdvice")
            : count > 0 ? (firstRun
                ? Str.T("Str.FirstRunAdviceIntro", count)
                : Str.T("Str.ConsoleReadyCount", count))
            : Str.T("Str.BasicsOk");
        GameReadyText.ToolTip = AppState.GamePath ?? Str.T("Str.NoGameSelected");
        LocateDeltaButton.Content = GameLocated && !GameReady ? Str.T("Str.RefreshDetect") : Str.T("Str.ConsoleLocateGame");
        PrepareDeltaButton.IsEnabled = GameLocated && !_refreshing && !_locating && !_preparing;
        PrepareDeltaButton.Content = count > 0
            ? Str.T("Str.ConsoleBasicAdviceCount", count)
            : Str.T("Str.ConsoleBasicAdvice");
        PrepareDeltaButton.ToolTip = Str.T("Str.BasicAdviceGuideTip");
        StartPreparationButton.IsEnabled = ConfigurePreparationButton.IsEnabled = !_preparing && !_refreshing && !_locating;
        try { PreparationPlanText.Text = PreparationPlanStore.Describe(PreparationPlanStore.Resolve(AppState.GamePath)); }
        catch (Exception ex)
        {
            PreparationPlanText.Text = PrivacyScrub.Sanitize(ex.Message);
            StartPreparationButton.IsEnabled = false;
        }
    }

    private async void LocateDelta_Click(object sender, RoutedEventArgs e)
    {
        if (_locating || _refreshing || Window.GetWindow(this) is not MainWindow main) return;
        _locating = true;
        LocateDeltaButton.IsEnabled = false;
        UpdatePreparation();
        try
        {
            if (GameLocated && !GameReady) { await RefreshDataAsync(); return; }
            // DetectAll 只返回已知游戏的主程序（各游戏 Shipping exe），不再限定三角洲
            var games = await Task.Run(() => GamePathService.DetectAll(refresh: true).ToList());
            string? path = games.Count == 1 ? games[0].ExePath : null;
            if (path is null)
            {
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Str.T("Str.PickGameExeHint"),
                    Filter = Str.T("Str.PickGameExeFilter"), CheckFileExists = true
                };
                if (picker.ShowDialog(main) != true) return;
                path = picker.FileName;
            }
            if (!System.IO.File.Exists(path) || !string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                DialogService.Warning(Str.T("Str.SelectGameMainExe"), Str.T("Str.MustPickGameExe"));
                return;
            }
            var profile = StateStore.AddGame(path);   // 0.2.0 M2：定位即建档（已有档幂等返回）
            GameContextService.SwitchTo(profile.ExePath);
            await RefreshDataAsync();
        }
        catch (Exception ex) { DialogService.Warning(Str.T("Str.GameLocateIncomplete"), PrivacyScrub.Sanitize(ex.Message)); }
        finally { _locating = false; LocateDeltaButton.IsEnabled = true; RefreshGameSwitcher(); UpdatePreparation(); }
    }

    private void PrepareDelta_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreparation();
        if (!PrepareDeltaButton.IsEnabled) return;
        if (Window.GetWindow(this) is not MainWindow main) return;

        var ids = DeltaPreparation.PendingItems(AppState.Items, GameReady);
        if (GameReady && ids.Count == 0)
        {
            DialogService.Info(Str.T("Str.BasicAdvice"), Str.T("Str.BasicAdviceAllReady"));
            return;
        }
        if (GameReady && StateStore.HasSeenOnboarding)
            main.ReviewSelection(ids);
        else
            // P3-9：首次先做一次只读体检（控制台概览此前直接跳优化、跳过了检测页）
            _ = main.ReviewSelectionWithIntroAsync(ids);
    }

    private void DeltaSession_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.OpenDeltaSession();
    }

    private void DeltaRestore_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("backup");
    }
    private void GameLibrary_Click(object sender, RoutedEventArgs e)
    { if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("games"); }

    public ConsoleView() : this(new GamePreparationService()) { }
    internal ConsoleView(GamePreparationService preparation)
    {
        _preparation = preparation;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            GameContextService.GameSwitched -= OnGameContextSwitched;
            StopMonitor();
            if (_owner is not null) _owner.IsVisibleChanged -= OwnerVisibilityChanged;
            _owner = null;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        GameContextService.GameSwitched += OnGameContextSwitched;
        RefreshGameSwitcher();
        _owner = Window.GetWindow(this);
        if (_owner is not null) _owner.IsVisibleChanged += OwnerVisibilityChanged;
        StartMonitor();
        UpdatePreparation();
        // 首页数据就绪信号（splash 放行条件）：首个实时样本落定后上报，避免进入界面仍是一片 "—"。
        TryNotifyHomeDataReady();
    }

    private bool _metricsSeen;

    private void TryNotifyHomeDataReady()
    {
        if (_metricsSeen && _owner is MainWindow main)
            main.NotifyHomeDataReady();
    }

    private async Task RefreshDataAsync()
    {
        if (_refreshing || Window.GetWindow(this) is not MainWindow main) return;
        _refreshing = true;
        UpdatePreparation();
        try
        {
            await main.RefreshDetectionAsync();
        }
        finally
        {
            _refreshing = false;
            UpdatePreparation();
        }
    }

    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) StartMonitor(); else StopMonitor();
    }

    private void StartMonitor()
    {
        if (_sampler is not null || !IsVisible) return;
        _sampler = App.LiveMetrics;
        _sampler.Sampled += ShowMetrics;
        App.UpdateLiveMetrics();
        if (_sampler.Buffer.LastOrDefault() is { } latest) ShowMetrics(latest);
    }

    private void StopMonitor()
    {
        if (_sampler is null) return;
        _sampler.Sampled -= ShowMetrics;
        _sampler = null;
        App.UpdateLiveMetrics();
    }

    private void ShowMetrics(MetricSample sample)
    {
        _metricsSeen = true;
        TryNotifyHomeDataReady();
        ShowMetric(CpuText, CpuMeter, sample.CpuPercent, "cpu");
        ShowMetric(MemoryText, MemoryMeter, sample.MemoryPercent, "mem");
        ShowMetric(GpuText, GpuMeter, sample.GpuPercent, "gpu");
    }

    private void ShowMetric(TextBlock text, ProgressBar meter, double? value, string key)
    {
        var available = value is { } n && double.IsFinite(n);
        text.Text = available ? $"{value:0}%" : "—";
        meter.Value = available ? Math.Clamp(value!.Value, 0, 100) : 0;
        text.ToolTip = available ? Str.T("Str.LiveLocalSampling") : _sampler?.UnavailableReasons.GetValueOrDefault(key) ?? Str.T("Str.LoadingMetrics");
    }

// ---------- 0.2.0 M2：多游戏切换（状态在 GameContextService，这里只管 UI） ----------

    private bool _suppressGameSwitch;

    private void RefreshGameSwitcher()
    {
        _suppressGameSwitch = true;
        try
        {
            var items = GameContextService.BuildSwitcherItems();
            GameSwitcher.ItemsSource = items;
            GameSwitcher.SelectedItem = GameContextService.SelectedItem(items);
        }
        finally
        {
            _suppressGameSwitch = false;
        }
    }

    private void GameSwitcher_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGameSwitch) return;
        if (GameSwitcher.SelectedItem is not GameSwitcherItem vm) return;
        if (vm.ProfileId is null)
        {
            var added = GameContextService.PickAndAddGame(Window.GetWindow(this));
            RefreshGameSwitcher();
            if (added is not null)
                GameContextService.SwitchTo(added.ExePath, detectIfMissing: true);
            return;
        }
        SwitchToGame(vm.ExePath!);
    }

    private void SwitchToGame(string exePath)
    {
        if (GameContextService.IsBusy)
        {
            RefreshGameSwitcher();
            DialogService.Warning(Str.T("Str.AppName"), Str.T("Str.SwitchBlockedDetecting"));
            return;
        }
        if (!GameContextService.SwitchTo(exePath)) RefreshGameSwitcher();
    }

    private void OnGameContextSwitched()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnGameContextSwitched);
            return;
        }
        RefreshGameSwitcher();
        UpdatePreparation();
    }

    // ---------- 首页：一键优化大卡 + 继续使用 + 常用入口 ----------

    private void HomeQuickCard_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("quick");
    }

    private void HomeQuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset }) return;
        QuickOptimizeView.PendingPreset = preset;
        if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("quick");
    }

    private void HomeSession_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("session");
    }

    private void HomeTools_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("tools");
    }

    /// <summary>“继续使用”最近访问 chips；由 MainWindow 在切回首页时调用刷新。</summary>
    internal void RefreshRecentPages()
    {
        if (RecentPagesPanel is null) return;
        RecentPagesPanel.Children.Clear();
        var recent = SettingsService.Current.RecentPages;
        RecentEmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var key in recent)
        {
            var title = PageTitle(key);
            if (title.Length == 0) continue;
            var chip = new Button
            {
                Content = title,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Height = 34,
                Margin = new Thickness(0, 0, 8, 8),
                Tag = key,
            };
            chip.Click += (_, _) => { if (Window.GetWindow(this) is MainWindow main) main.NavigateTo(key); };
            RecentPagesPanel.Children.Add(chip);
        }
    }

    private static string PageTitle(string key) => key switch
    {
        "games" => Str.T("Str.NavGames"),
        "quick" => Str.T("Str.QuickOptTitle"),
        "detect" => Str.T("Str.NavDetect"),
        "opt" => Str.T("Str.NavOpt"),
        "display" => Str.T("Str.NavDisplayPage"),
        "filters" => Str.T("Str.NavFilters"),
        "session" => Str.T("Str.NavSession"),
        "ab" => Str.T("Str.NavAb"),
        "backup" => Str.T("Str.NavBackup"),
        "startup" => Str.T("Str.NavStartup"),
        "tools" => Str.T("Str.NavTools"),
        "settings" => Str.T("Str.NavSettings"),
        _ => "",
    };
}
