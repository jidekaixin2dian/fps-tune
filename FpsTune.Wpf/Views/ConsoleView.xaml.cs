using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ConsoleView : UserControl
{
    private sealed record Row(string Number, OptimizationItemViewModel Item);
    private List<Row> _rows = new();
    private List<OptimizationItem>? _source;
    private MetricsSampler? _sampler;
    private Window? _owner;
    private string _preset = "balanced";
    private bool _changingSelection;
    private bool _refreshing;
    private bool _locating;

    private bool DeltaLocated => DeltaPreparation.IsGameExecutable(AppState.GamePath)
        && System.IO.File.Exists(AppState.GamePath);
    private bool DeltaReady => DeltaLocated && AppState.Items.Count > 0
        && string.Equals(AppState.DetectJson?["gamePath"]?.GetValue<string>(), AppState.GamePath, StringComparison.OrdinalIgnoreCase);

    private void UpdatePreparation()
    {
        var count = DeltaPreparation.PendingItems(AppState.Items, DeltaReady).Count;
        GameReadyText.Text = _locating ? "正在定位并更新检测…" : !DeltaReady
            ? DeltaLocated ? "已选择三角洲 · 请更新检测，获取当前目标的基础建议。" : "先定位三角洲主程序，再获取对应的基础建议。"
            : count > 0 ? $"游戏已就绪 · {count} 项基础设置待审阅；可先记录一局作为对照。"
            : "基础设置已达标 · 可记录一局负载，保留优化前后对照。";
        GameReadyText.ToolTip = AppState.GamePath ?? "尚未选择游戏";
        LocateDeltaButton.Content = DeltaLocated && !DeltaReady ? "更新检测" : "定位游戏";
        PrepareDeltaButton.IsEnabled = DeltaReady && count > 0 && !_refreshing && !_locating;
        PrepareDeltaButton.Content = count > 0 ? $"基础建议 · {count}" : "基础建议";
    }

    private async void LocateDelta_Click(object sender, RoutedEventArgs e)
    {
        if (_locating || _refreshing || Window.GetWindow(this) is not MainWindow main) return;
        _locating = true;
        LocateDeltaButton.IsEnabled = false;
        UpdatePreparation();
        try
        {
            if (DeltaLocated && !DeltaReady) { await RefreshDataAsync(); return; }
            var games = await Task.Run(() => GamePathService.DetectAll(refresh: true)
                .Where(g => DeltaPreparation.IsGameExecutable(g.ExePath)).ToList());
            string? path = games.Count == 1 ? games[0].ExePath : null;
            if (path is null)
            {
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择三角洲主程序（不是启动器，通常位于 Game\\Binaries\\Win64）",
                    Filter = "三角洲游戏主程序|DeltaForceClient-Win64-Shipping.exe", CheckFileExists = true
                };
                if (picker.ShowDialog(main) != true) return;
                path = picker.FileName;
            }
            if (!DeltaPreparation.IsGameExecutable(path) || !System.IO.File.Exists(path))
            {
                DialogService.Warning("请选择游戏主程序", "需要选择 DeltaForceClient-Win64-Shipping.exe，不能选择启动器。");
                return;
            }
            StateStore.SaveGamePath(path);
            if (!string.Equals(StateStore.LoadGamePath(), path, StringComparison.OrdinalIgnoreCase))
                throw new System.IO.IOException("游戏路径未能保存，请检查本地设置目录权限。");
            AppState.GamePath = path;
            await RefreshDataAsync();
        }
        catch (Exception ex) { DialogService.Warning("游戏定位未完成", ex.Message); }
        finally { _locating = false; LocateDeltaButton.IsEnabled = true; UpdatePreparation(); }
    }

    private void PrepareDelta_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreparation();
        if (!PrepareDeltaButton.IsEnabled) return;
        if (Window.GetWindow(this) is MainWindow main)
            main.ReviewSelection(DeltaPreparation.PendingItems(AppState.Items, DeltaReady));
    }

    private void DeltaSession_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.OpenDeltaSession();
    }

    private void DeltaRestore_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.NavigateTo("backup");
    }

    public ConsoleView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            StopMonitor();
            if (_owner is not null) _owner.IsVisibleChanged -= OwnerVisibilityChanged;
            _owner = null;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this);
        if (_owner is not null) _owner.IsVisibleChanged += OwnerVisibilityChanged;
        StartMonitor();
        RebuildRows();
        if (AppState.Items.Count == 0) await RefreshDataAsync();
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
        if (_sampler.Buffer.LastOrDefault() is { } latest) ShowMetrics(latest);
    }

    private void StopMonitor()
    {
        if (_sampler is null) return;
        _sampler.Sampled -= ShowMetrics;
        _sampler = null;
    }

    private void ShowMetrics(MetricSample sample)
    {
        ShowMetric(CpuText, CpuMeter, sample.CpuPercent, "cpu");
        ShowMetric(MemoryText, MemoryMeter, sample.MemoryPercent, "mem");
        ShowMetric(GpuText, GpuMeter, sample.GpuPercent, "gpu");
    }

    private void ShowMetric(TextBlock text, ProgressBar meter, double? value, string key)
    {
        var available = value is { } n && double.IsFinite(n);
        text.Text = available ? $"{value:0}%" : "—";
        meter.Value = available ? Math.Clamp(value!.Value, 0, 100) : 0;
        text.ToolTip = available ? "实时本地采样" : _sampler?.UnavailableReasons.GetValueOrDefault(key) ?? "正在读取指标";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

    private async Task RefreshDataAsync()
    {
        if (_refreshing || Window.GetWindow(this) is not MainWindow main) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        ReviewButton.IsEnabled = false;
        StateText.Text = "正在后台检测，仍可切换页面…";
        try
        {
            var ok = await main.RefreshDetectionAsync();
            RebuildRows();
            StateText.Text = ok ? "检测已更新。开关仅选择项目，审阅后执行；修改前自动备份。" : "检测未完成，可重新检测或前往检测页查看原因。";
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            UpdateSelection();
        }
    }

    private void RebuildRows()
    {
        UpdatePreparation();
        if (ReferenceEquals(_source, AppState.Items)) return;
        var selected = _rows.Where(r => r.Item.IsChecked).Select(r => r.Item.Id).ToHashSet();
        foreach (var row in _rows) row.Item.PropertyChanged -= ItemChanged;
        _source = AppState.Items;
        _rows = _source.Select((item, index) => new Row($"{index + 1:00}", new OptimizationItemViewModel(item))).ToList();
        foreach (var row in _rows) row.Item.PropertyChanged += ItemChanged;
        SetChecks(_preset == "custom" ? selected : OptimizationCatalog.ResolvePreset(_preset).ToHashSet());
        var count = _source.Count(i => i.Optimized);
        TunedText.Text = _source.Count > 0 ? $"{count}/{_source.Count}" : "—";
        TunedMeter.Value = _source.Count > 0 ? 100.0 * count / _source.Count : 0;
        FilterRows();
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string preset }) return;
        _preset = preset;
        if (ItemList is null || preset == "custom") return;
        SetChecks(OptimizationCatalog.ResolvePreset(preset).ToHashSet());
    }

    private void SetChecks(HashSet<string> selected)
    {
        _changingSelection = true;
        foreach (var row in _rows) row.Item.IsChecked = selected.Contains(row.Item.Id);
        _changingSelection = false;
        UpdateSelection();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_changingSelection || e.PropertyName != nameof(OptimizationItemViewModel.IsChecked)) return;
        CustomPreset.IsChecked = true;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        UpdatePreparation();
        var count = _rows.Count(r => r.Item.IsChecked);
        SelectionText.Text = _rows.Count == 0 ? "尚无检测结果" : $"已选择 {count} 项 / 共 {_rows.Count} 项";
        ReviewButton.IsEnabled = count > 0 && !_refreshing;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => FilterRows();
    private void FilterRows()
    {
        if (ItemList is null || SearchBox is null) return;
        var query = SearchBox.Text.Trim();
        ItemList.ItemsSource = _rows.Where(r => r.Item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || r.Item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.ReviewSelection(_rows.Where(r => r.Item.IsChecked).Select(r => r.Item.Id).ToArray());
    }
}
