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
        var count = DeltaPreparation.PendingItems(AppState.Items, GameReady).Count;
        GameReadyTitle.Text = GameLocated
            ? $"{GameContextService.DisplayNameFor(AppState.GamePath!)} · 开局准备"
            : "开局准备";
        GameReadyText.Text = _locating ? Str.T("Str.LocatingAndDetecting") : !GameReady
            ? GameLocated ? Str.T("Str.GameSelectedRefreshDetect") : Str.T("Str.LocateGameForAdvice")
            : Str.T(count > 0 ? "Str.HomeReadyPending" : "Str.HomeReady");
        GameReadyText.ToolTip = AppState.GamePath ?? Str.T("Str.NoGameSelected");
        LocateDeltaButton.Content = GameLocated && !GameReady ? Str.T("Str.RefreshDetect") : "定位游戏";
        OpenOptimizationButton.IsEnabled = !_preparing && !_refreshing && !_locating;
        StartPreparationButton.IsEnabled = ConfigurePreparationButton.IsEnabled = OpenOptimizationButton.IsEnabled;
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
                DialogService.Warning("请选择游戏主程序", Str.T("Str.MustPickGameExe"));
                return;
            }
            var profile = StateStore.AddGame(path);   // 0.2.0 M2：定位即建档（已有档幂等返回）
            GameContextService.SwitchTo(profile.ExePath);
            await RefreshDataAsync();
        }
        catch (Exception ex) { DialogService.Warning(Str.T("Str.GameLocateIncomplete"), ex.Message); }
        finally { _locating = false; LocateDeltaButton.IsEnabled = true; RefreshGameSwitcher(); UpdatePreparation(); }
    }

    private void OpenOptimization_Click(object sender, RoutedEventArgs e)
    { if (OpenOptimizationButton.IsEnabled && Window.GetWindow(this) is MainWindow main) main.NavigateTo("opt"); }
    private void PrepareDelta_Click(object sender, RoutedEventArgs e) => OpenOptimization_Click(sender, e);
    private void DisplayShortcut_Click(object sender, RoutedEventArgs e)
    { if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("display"); }
    private void HudShortcut_Click(object sender, RoutedEventArgs e)
    { if (Window.GetWindow(this) is MainWindow main) main.ToggleHudFromHome(); }
    private void MaintenanceShortcut_Click(object sender, RoutedEventArgs e)
    { if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("tools"); }

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
        RebuildRows();
        if (AppState.Items.Count == 0) await RefreshDataAsync();
        // 首页数据就绪信号（splash 放行条件之一）：概览与检测状态落定；
        // 与首个实时样本汇合后才上报，避免进入界面仍是一片 "—"。
        _dataSettled = true;
        TryNotifyHomeDataReady();
    }

    private bool _metricsSeen;
    private bool _dataSettled;

    private void TryNotifyHomeDataReady()
    {
        if (_metricsSeen && _dataSettled && _owner is MainWindow main)
            main.NotifyHomeDataReady();
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

    private async Task RefreshDataAsync()
    {
        if (_refreshing || Window.GetWindow(this) is not MainWindow main) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        UpdatePreparation();
        StateText.Text = Str.T("Str.DetectingBackground");
        try
        {
            var ok = await main.RefreshDetectionAsync();
            RebuildRows();
            StateText.Text = ok ? Str.T("Str.DetectUpdatedHint") : Str.T("Str.DetectIncompleteRetry");
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            UpdatePreparation();
        }
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
        RebuildRows();
        RefreshGameSwitcher();
    }

    private void RebuildRows()
    {
        RefreshGameSwitcher(); UpdatePreparation();
        var available = AppState.Items.Where(i => ItemCatalog.All.FirstOrDefault(d => d.Id == i.Id)?.Available != false).ToArray();
        var count = available.Count(i => i.Optimized);
        TunedText.Text = available.Length > 0 ? $"{count}/{available.Length}" : "—";
        TunedMeter.Value = available.Length > 0 ? 100.0 * count / available.Length : 0;
    }
}
