using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using System.Linq;

namespace FpsTune.Wpf.Views;

public partial class DetectView : UserControl
{
    private sealed record CheckItemViewModel(string Name, string Summary, Brush Foreground);

    private bool _hasSavedState;
    private bool _detectionInFlight;

    private MetricsSampler? _sampler;
    private bool _suppressGameSwitch;
    private Window? _hostWindow;

    public DetectView()
    {
        InitializeComponent();
        LoadSavedState();
        RefreshLastDetectText();
        Loaded += (_, _) =>
        {
            if (!_hasSavedState)
            {
                _ = RunDetectionAsync();
            }
            StartMonitor();
            StartGameScan();
            if ((_hostWindow = Window.GetWindow(this)) is { } window)
                window.IsVisibleChanged += HostWindow_IsVisibleChanged;
        };
        Unloaded += (_, _) =>
        {
            StopMonitor();
            if (_hostWindow is { } window)
                window.IsVisibleChanged -= HostWindow_IsVisibleChanged;
            _hostWindow = null;
        };
    }

    // 托盘隐藏调用 Window.Hide(), 不触发 Unloaded, 需按可见性联动采样计时器
    private void HostWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            StartMonitor();
        else
            StopMonitor();
    }

    // ---------- 实时监控（共享应用采样，页面隐藏只取消订阅） ----------

    private void StartMonitor()
    {
        if (_sampler is not null || !IsVisible)
            return;
        _sampler = App.LiveMetrics;
        _sampler.Sampled += Sampler_Sampled;
        if (_sampler.Buffer.LastOrDefault() is { } latest) Sampler_Sampled(latest);
    }

    private void StopMonitor()
    {
        if (_sampler is null)
            return;
        _sampler.Sampled -= Sampler_Sampled;
        _sampler = null;
    }

    private void Sampler_Sampled(MetricSample sample)
    {
        var cpu = _sampler?.Buffer.Select(s => s.CpuPercent ?? double.NaN).ToList();
        var mem = _sampler?.Buffer.Select(s => s.MemoryPercent ?? double.NaN).ToList();
        var gpu = _sampler?.Buffer.Select(s => s.GpuPercent ?? double.NaN).ToList();
        var vramRatio = BuildVramSeries();

        if (cpu is not null) MiniChart.Draw(CpuChart, cpu, "AccentBrush");
        if (mem is not null) MiniChart.Draw(MemChart, mem, "PrimaryBrush");
        if (gpu is not null) MiniChart.Draw(GpuChart, gpu, "OkBrush");
        if (vramRatio is not null) MiniChart.Draw(VramChart, vramRatio, "WarningBrush");

        CpuNowText.Text = Fmt(sample.CpuPercent);
        MemNowText.Text = Fmt(sample.MemoryPercent);
        GpuNowText.Text = Fmt(sample.GpuPercent);
        if (sample.VramUsedBytes is { } used)
        {
            VramNowText.Text = sample.VramTotalBytes is { } total && total > 0
                ? $"{used / total * 100.0:0}%"
                : $"{used / 1024.0 / 1024.0:0} MiB";
            VramNoteText.Text = sample.VramTotalBytes is { } t2 && t2 > 0
                ? $"{used / 1024.0 / 1024.0:0} / {t2 / 1024.0 / 1024.0:0} MiB"
                : "容量不可得，仅显示用量";
        }
        else
        {
            VramNowText.Text = Str.T("Str.Unavailable");
            VramNoteText.Text = _sampler?.UnavailableReasons.TryGetValue("vram", out var reason) == true
                ? reason
                : "未提供 GPU Adapter Memory 计数器";
        }
    }

    /// <summary>显存序列：容量可得时为占比 0-100，否则为窗口内相对水位。</summary>
    private List<double>? BuildVramSeries()
    {
        if (_sampler is null)
            return null;
        var buffer = _sampler.Buffer;
        if (buffer.Count == 0)
            return [];
        if (buffer[0].VramTotalBytes is { } total && total > 0)
            return buffer.Select(s => s.VramUsedBytes is { } u ? u / total * 100.0 : double.NaN).ToList();
        var peak = buffer.Where(s => s.VramUsedBytes is not null).Select(s => s.VramUsedBytes ?? 0).DefaultIfEmpty(0).Max();
        if (peak <= 0)
            return [];
        return buffer.Select(s => s.VramUsedBytes is { } u ? u / peak * 100.0 : double.NaN).ToList();
    }

    private static string Fmt(double? v) => v is double d && double.IsFinite(d) ? $"{d:0}%" : "--";

    // ---------- 游戏切换 ----------

    private void StartGameScan(bool refresh = false)
    {
        if (_suppressGameSwitch)
            return;
        _suppressGameSwitch = true;
        GameSwitcher.Items.Clear();
        GameSwitcher.Items.Add(new ComboBoxItem { Content = Str.T("Str.Scanning"), IsEnabled = false });
        GameSwitcher.SelectedIndex = 0;
        _suppressGameSwitch = false;

        Task.Run(async () =>
        {
            try
            {
                var games = GamePathService.DetectAll(refresh);
                await Dispatcher.InvokeAsync(() => FillGameSwitcher(games));
            }
            catch
            {
                await Dispatcher.InvokeAsync(() => FillGameSwitcher(new List<GamePathService.GameCandidate>()));
            }
        });
    }

    private void FillGameSwitcher(IReadOnlyList<GamePathService.GameCandidate> games)
    {
        _suppressGameSwitch = true;
        GameSwitcher.Items.Clear();
        foreach (var g in games)
            GameSwitcher.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.ExePath, ToolTip = g.ExePath });
        if (games.Count == 0)
            GameSwitcher.Items.Add(new ComboBoxItem { Content = Str.T("Str.NoInstalledGame"), IsEnabled = false });
        GameSwitcher.Items.Add(new ComboBoxItem { Content = Str.T("Str.ManualSpecify"), Tag = "manual" });

        var current = StateStore.LoadGamePath() ?? AppState.GamePath;
        var idx = -1;
        if (!string.IsNullOrWhiteSpace(current))
        {
            // 保存的路径可能指向同游戏的另一层 exe(启动器 vs Shipping 真进程):
            // 依次按 精确路径 → 文件名 → 显示名 匹配
            var currentName = System.IO.Path.GetFileName(current);
            var currentLabel = GamePathService.LabelFor(current);
            for (var i = 0; i < GameSwitcher.Items.Count; i++)
            {
                if (GameSwitcher.Items[i] is not ComboBoxItem ci || ci.Tag is not string path || path.Length == 0)
                    continue;
                if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.GetFileName(path).Equals(currentName, StringComparison.OrdinalIgnoreCase)
                    || GamePathService.LabelFor(path) == currentLabel)
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                // 保存路径不在候选里: 把它自身列为当前项, 避免下拉框出现无解释的空白
                GameSwitcher.Items.Insert(0, new ComboBoxItem { Content = currentLabel, Tag = current, ToolTip = current });
                idx = 0;
            }
        }
        GameSwitcher.SelectedIndex = idx;
        _suppressGameSwitch = false;
        RefreshGamePathText();
    }

    private void GameSwitcher_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGameSwitch)
            return;
        if (GameSwitcher.SelectedItem is not ComboBoxItem ci)
            return;
        if (ci.Tag as string == "manual")
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = Str.T("Str.SelectGameMainExe"), Filter = "可执行文件 (*.exe)|*.exe" };
            if (dlg.ShowDialog() == true)
                ApplyGame(dlg.FileName);
            else
                StartGameScan(); // 取消选择, 恢复显示当前值
            return;
        }
        if (ci.Tag is string path && path.Length > 0)
            ApplyGame(path);
    }

    private void ApplyGame(string path)
    {
        StateStore.SaveGamePath(path);
        AppState.GamePath = path;
        RefreshGamePathText();
    }

    private void RescanGames_Click(object sender, RoutedEventArgs e) => StartGameScan(refresh: true);

    private void RefreshGamePathText()
    {
        var path = StateStore.LoadGamePath() ?? AppState.GamePath;
        GamePathText.Text = string.IsNullOrWhiteSpace(path)
            ? Str.T("Str.NoInstalledGameHint")
            : path;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
        => _ = await RunDetectionAsync();

    public Task<bool> RunOnboardingDetectionAsync()
        => RunDetectionAsync();

    internal async Task<bool> RunDetectionAsync()
    {
        if (_detectionInFlight)
            return false;
        _detectionInFlight = true;

        RunButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        OutputBox.Text = Str.T("Str.Detecting");
        LastDetectText.Text = Str.T("Str.Detecting");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await OptimizationEngine.DetectAsync();
            sw.Stop();
            if (!result.Success)
            {
                OutputBox.Text = result.Error + Environment.NewLine + result.Output;
                RefreshLastDetectText();   // 失败不记录，恢复显示上一次的成功结果
                return false;
            }

            var root = JsonNode.Parse(result.Output)?.AsObject();
            if (root is null)
            {
                OutputBox.Text = Str.T("Str.CannotParseDetectJson");
                RefreshLastDetectText();
                return false;
            }

            StateStore.SaveDetect(root);
            StateStore.SaveDetectMeta(DateTime.Now, sw.ElapsedMilliseconds);
            // 0.2.0 M2：同步写入当前游戏的逐游戏快照（未建档的游戏只写全局，不建档）。
            var currentGameId = StateStore.FindGameByPath(AppState.GamePath)?.Id;
            if (currentGameId is not null)
            {
                StateStore.SaveDetectForGame(currentGameId, root);
                StateStore.SaveDetectMetaForGame(currentGameId, new StateStore.DetectMeta(DateTime.Now, sw.ElapsedMilliseconds));
            }
            ApplyDetectData(root, showDetails: true);
            RefreshLastDetectText();
            _hasSavedState = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.ToString();
            RefreshLastDetectText();   // 失败不记录，恢复显示上一次的成功结果
            return false;
        }
        finally
        {
            _detectionInFlight = false;
            RunButton.IsEnabled = true;
            LoadButton.IsEnabled = true;
        }
    }

    private void LoadSavedState()
    {
        // 0.2.0 M2：优先当前游戏的逐游戏快照；没有（未建档/未检测）再回退全局 last-detect.json。
        var gid = StateStore.FindGameByPath(AppState.GamePath)?.Id;
        var saved = gid is null
            ? StateStore.LoadDetect()
            : (StateStore.LoadDetectForGame(gid) ?? StateStore.LoadDetect());
        if (saved is not JsonObject root)
            return;

        _hasSavedState = true;
        ApplyDetectData(root, showDetails: false);
        OutputBox.Text = Str.T("Str.LoadedLastScan");
    }

    /// <summary>0.2.0 M2：概览页切换游戏后同步本页——有快照灌快照，无快照清空待自动检测。</summary>
    public void OnGameSwitched()
    {
        if (_detectionInFlight) return;
        var gid = StateStore.FindGameByPath(AppState.GamePath)?.Id;
        var root = gid is null ? null : StateStore.LoadDetectForGame(gid);
        if (root is not null)
        {
            ApplyDetectData(root, showDetails: false);
            _hasSavedState = true;
        }
        else
        {
            _hasSavedState = false;
            ResetDetectedState();
        }
        RefreshLastDetectText();
    }

    private void ResetDetectedState()
    {
        AppState.DetectJson = null;
        AppState.Items = new List<OptimizationItem>();
        DetectItemList.ItemsSource = new List<OptimizationItemViewModel>();
        CheckList.ItemsSource = new List<object>();
        CpuText.Text = "--"; GpuText.Text = "--"; RamText.Text = "--"; OsText.Text = "--";
        LaptopText.Text = "--"; AdminText.Text = "--";
        GamePathText.Text = AppState.GamePath ?? "--";
        OutputBox.Text = Str.T("Str.GameSwitchNeedDetect");
    }

    /// <summary>P3-4：页头状态行——上次检测的完成时间与耗时（未检测过时给出动作指引）。</summary>
    private void RefreshLastDetectText()
    {
        // 0.2.0 M2：显示当前游戏的检测元数据（无逐游戏快照时回退全局）。
        var meta = StateStore.FindGameByPath(AppState.GamePath) is { } g
            ? StateStore.LoadDetectMetaForGame(g.Id)
            : StateStore.LoadDetectMeta();
        LastDetectText.Text = meta is null
            ? Str.T("Str.NeverDetected")
            : Str.T("Str.LastDetectLabel",
                meta.At.ToString("yyyy-MM-dd HH:mm"),
                meta.ElapsedMs / 1000.0);
    }

    private void ApplyDetectData(JsonObject root, bool showDetails)
    {
        AppState.DetectJson = root;
        var hardware = root["hardware"]?.AsObject();
        if (hardware is not null)
        {
            CpuText.Text = hardware["cpu"]?.GetValue<string>() ?? "--";
            GpuText.Text = hardware["gpu"]?.GetValue<string>() ?? "--";
            var ramNode = hardware["ramGB"];
            RamText.Text = ramNode is null ? "--" : ramNode.ToString() + " GB";
            OsText.Text = hardware["os"]?.GetValue<string>() ?? "--";
            LaptopText.Text = hardware["isLaptop"]?.GetValue<bool>() == true ? Str.T("Str.Laptop") : Str.T("Str.Desktop");
            AdminText.Text = hardware["isAdmin"]?.GetValue<bool>() == true ? Str.T("Str.Admin") : Str.T("Str.NormalUser");
        }

        AppState.GamePath = root["gamePath"]?.GetValue<string>();
        GamePathText.Text = AppState.GamePath ?? "--";

        var checks = root["checks"]?.AsArray();
        CheckList.ItemsSource = BuildCheckItems(checks);

        // 0.2.0 M2：解析逻辑抽到 DetectionData 共用（概览页切游戏灌快照走同一口径）。
        AppState.Items = DetectionData.ParseItems(root);
        var viewModels = AppState.Items
            .Select(i => new OptimizationItemViewModel(i))
            .ToList();
        DetectItemList.ItemsSource = viewModels;

        if (!showDetails)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("== 硬件 ==");
        sb.AppendLine($"CPU: {CpuText.Text}");
        sb.AppendLine($"GPU: {GpuText.Text}");
        sb.AppendLine($"内存: {RamText.Text}");
        sb.AppendLine($"系统: {OsText.Text}");
        sb.AppendLine($"笔记本: {LaptopText.Text}  管理员: {AdminText.Text}");
        sb.AppendLine($"游戏路径: {GamePathText.Text}");
        sb.AppendLine();
        sb.AppendLine("== 体检 ==");
        if (checks is not null)
        {
            foreach (var c in checks)
                sb.AppendLine($"[{c?["status"]?.GetValue<string>()}] {c?["name"]?.GetValue<string>()}: {c?["message"]?.GetValue<string>()}");
        }
        sb.AppendLine();
        sb.AppendLine($"== 优化项（{AppState.Items.Count} 项） ==");
        foreach (var item in AppState.Items)
        {
            var status = item.Optimized ? "[已达标]" : "[未应用]";
            sb.AppendLine($"{status} {item.Id}  {item.Name}");
            if (!string.IsNullOrWhiteSpace(item.Description))
                sb.AppendLine($"      说明：{item.Description}");
            if (!string.IsNullOrWhiteSpace(item.SideEffect))
                sb.AppendLine($"      副作用：{item.SideEffect}");
            if (!string.IsNullOrWhiteSpace(item.Current))
                sb.AppendLine($"      当前：{item.Current}");
            var req = (item.RequiresAdmin ? Str.T("Str.Admin") : Str.T("Str.NormalUser")) +
                      (item.RequiresReboot ? "，需重启" : "");
            sb.AppendLine($"      要求：{req}");
            sb.AppendLine();
        }
        sb.AppendLine(Str.T("Str.RawJsonElsewhere"));
        OutputBox.Text = sb.ToString();
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.DetectJson is null)
        {
            DialogService.Info("提示", Str.T("Str.RunDetectFirst"));
            return;
        }

        var window = Window.GetWindow(this);
        if (window is MainWindow main)
            main.ShowOptimizePage();
        DialogService.Info("提示", $"已加载 {AppState.Items.Count} 个优化项到优化页。");
    }

    private void CopyGamePath_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AppState.GamePath))
        {
            Clipboard.SetText(AppState.GamePath);
            DialogService.Info(Str.T("Str.GamePath"), Str.T("Str.CopiedGamePath"));
        }
        else
        {
            DialogService.Info(Str.T("Str.GamePath"), Str.T("Str.NoGamePathToCopy"));
        }
    }

    private void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            Clipboard.SetText(OutputBox.Text);
            DialogService.Info(Str.T("Str.DetectDetails"), Str.T("Str.CopiedDetails"));
        }
    }

    // ---------- 页面滚轮穿透 ----------

    // 概览列表在根 ScrollViewer 里按自然高度完全展开(自身永不需要滚动),
    // 但其内部 ScrollViewer 仍会把鼠标滚轮标记为已处理, 页面因此"滚几格就停"。
    // 统一在 Preview 阶段转发给根滚动。
    private void ForwardWheelToRoot(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // JSON 详情框自身可滚: 仅当已滚到对应方向的尽头时把滚轮还给页面。
    private void TextBoxWheelToRoot(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        var atTop = OutputBox.VerticalOffset <= 0.1;
        var atBottom = OutputBox.VerticalOffset >= OutputBox.ExtentHeight - OutputBox.ViewportHeight - 0.1;
        var leavingDown = e.Delta < 0 && atBottom;
        var leavingUp = e.Delta > 0 && atTop;
        if (!leavingDown && !leavingUp)
            return; // 框内还有内容可滚, 保留默认行为
        RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static IReadOnlyList<CheckItemViewModel> BuildCheckItems(JsonArray? checks)
    {
        if (checks is null || checks.Count == 0)
        {
            return new[]
            {
                new CheckItemViewModel("体检", Str.T("Str.StatusNotDetected"), GetStatusBrush(string.Empty))
            };
        }

        return checks.Select(node =>
        {
            var name = node?["name"]?.GetValue<string>() ?? "体检项";
            var status = node?["status"]?.GetValue<string>() ?? string.Empty;
            var message = node?["message"]?.GetValue<string>() ?? string.Empty;
            var statusLabel = status switch { "ok" => Str.T("Str.StatusOk"), "attention" => Str.T("Str.StatusPending"), "danger" => Str.T("Str.StatusError"), _ => status };
            var summary = string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(message)
                ? Str.T("Str.StatusNotDetected")
                : $"● {statusLabel}  {message}".Trim();
            return new CheckItemViewModel(name, summary, GetStatusBrush(status));
        }).ToList();
    }

    private static Brush GetStatusBrush(string status)
    {
        var key = status switch
        {
            "ok" => "OkBrush",
            "attention" => "WarningBrush",
            "danger" => "DangerBrush",
            _ => "TextSecondaryBrush"
        };

        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;
        return Brushes.Gray;
    }
}
