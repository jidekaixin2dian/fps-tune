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

    private System.Windows.Threading.DispatcherTimer? _monitorTimer;
    private readonly List<double> _cpuHist = new();
    private readonly List<double> _memHist = new();
    private readonly List<double> _gpuHist = new();
    private bool _suppressGameSwitch;

    public DetectView()
    {
        InitializeComponent();
        LoadSavedState();
        Loaded += (_, _) =>
        {
            if (!_hasSavedState)
            {
                _ = RunDetectionAsync();
            }
            StartMonitor();
            StartGameScan();
            if (Window.GetWindow(this) is { } window)
                window.IsVisibleChanged += HostWindow_IsVisibleChanged;
        };
        Unloaded += (_, _) =>
        {
            StopMonitor();
            if (Window.GetWindow(this) is { } window)
                window.IsVisibleChanged -= HostWindow_IsVisibleChanged;
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

    // ---------- 实时监控 ----------

    private void StartMonitor()
    {
        if (_monitorTimer is not null)
            return;
        _monitorTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(UiPerformance.LowSpec ? 3 : 1) };
        _monitorTimer.Tick += (_, _) => SampleMonitor();
        _monitorTimer.Start();
    }

    private void StopMonitor()
    {
        _monitorTimer?.Stop();
        _monitorTimer = null;
    }

    private void SampleMonitor()
    {
        var (cpu, mem, gpu) = LiveMetrics.ReadOnce();
        Push(_cpuHist, cpu);
        Push(_memHist, mem);
        Push(_gpuHist, gpu);
        CpuNowText.Text = Fmt(cpu);
        MemNowText.Text = Fmt(mem);
        GpuNowText.Text = Fmt(gpu);
        DrawChart(CpuChart, _cpuHist, "AccentBrush");
        DrawChart(MemChart, _memHist, "PrimaryBrush");
        DrawChart(GpuChart, _gpuHist, "OkBrush");
    }

    private static string Fmt(double v) => double.IsFinite(v) ? $"{v:0}%" : "--";

    private static void Push(List<double> history, double v)
    {
        if (double.IsFinite(v))
            history.Add(Math.Clamp(v, 0, 100));
        while (history.Count > 60)
            history.RemoveAt(0);
    }

    private void DrawChart(System.Windows.Controls.Canvas canvas, List<double> history, string brushKey)
    {
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 10)
            return;
        canvas.Children.Clear();

        // 底线与半高线
        for (var i = 0; i < 2; i++)
        {
            var y = i == 0 ? h - 1 : h / 2;
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = (Brush)Application.Current.Resources["BorderBrush"],
                StrokeThickness = 1,
                Opacity = 0.5
            });
        }

        if (history.Count == 0)
            return;

        var stroke = (Brush)Application.Current.Resources[brushKey];
        double Step() => w / Math.Max(60 - 1, history.Count - 1);
        var offset = 60 - history.Count;
        var points = new System.Windows.Media.PointCollection();
        for (var i = 0; i < history.Count; i++)
            points.Add(new Point(offset * Step() + i * Step(), h - 2 - (h - 4) * history[i] / 100));
        canvas.Children.Add(new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = stroke,
            StrokeThickness = 1.6,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        });

        var last = history[^1];
        canvas.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6,
            Fill = stroke,
            Margin = new Thickness(points[^1].X - 3, points[^1].Y - 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        _ = last;
    }

    // ---------- 游戏切换 ----------

    private void StartGameScan(bool refresh = false)
    {
        if (_suppressGameSwitch)
            return;
        _suppressGameSwitch = true;
        GameSwitcher.Items.Clear();
        GameSwitcher.Items.Add(new ComboBoxItem { Content = "扫描中…", IsEnabled = false });
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
            GameSwitcher.Items.Add(new ComboBoxItem { Content = "未检测到已安装游戏", IsEnabled = false });
        GameSwitcher.Items.Add(new ComboBoxItem { Content = "手动指定…", Tag = "manual" });

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
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择游戏主程序", Filter = "可执行文件 (*.exe)|*.exe" };
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
            ? "未检测到已安装游戏，可在下方切换或手动指定"
            : path;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
        => _ = await RunDetectionAsync();

    public Task<bool> RunOnboardingDetectionAsync()
        => RunDetectionAsync();

    private async Task<bool> RunDetectionAsync()
    {
        if (_detectionInFlight)
            return false;
        _detectionInFlight = true;

        RunButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        OutputBox.Text = "正在检测...";

        try
        {
            var result = await OptimizationEngine.DetectAsync();
            if (!result.Success)
            {
                OutputBox.Text = result.Error + Environment.NewLine + result.Output;
                return false;
            }

            var root = JsonNode.Parse(result.Output)?.AsObject();
            if (root is null)
            {
                OutputBox.Text = "无法解析检测 JSON。";
                return false;
            }

            StateStore.SaveDetect(root);
            ApplyDetectData(root, showDetails: true);
            _hasSavedState = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.ToString();
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
        var saved = StateStore.LoadDetect();
        if (saved is not JsonObject root)
            return;

        _hasSavedState = true;
        ApplyDetectData(root, showDetails: false);
        OutputBox.Text = "已加载上次扫描结果。点击“运行检测”可重新扫描。";
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
            LaptopText.Text = hardware["isLaptop"]?.GetValue<bool>() == true ? "是" : "否";
            AdminText.Text = hardware["isAdmin"]?.GetValue<bool>() == true ? "是" : "否";
        }

        AppState.GamePath = root["gamePath"]?.GetValue<string>();
        GamePathText.Text = AppState.GamePath ?? "--";

        var checks = root["checks"]?.AsArray();
        CheckList.ItemsSource = BuildCheckItems(checks);

        AppState.Items.Clear();
        var items = root["items"]?.AsArray();
        if (items is not null)
        {
            foreach (var item in items)
            {
                var id = item?["id"]?.GetValue<string>() ?? "";
                var name = item?["name"]?.GetValue<string>() ?? "";
                var desc = item?["desc"]?.GetValue<string>()
                    ?? item?["description"]?.GetValue<string>()
                    ?? "";
                var sideEffect = item?["sideEffect"]?.GetValue<string>() ?? "";
                var admin = item?["requiresAdmin"]?.GetValue<bool>()
                    ?? item?["needsAdmin"]?.GetValue<bool>()
                    ?? item?["admin"]?.GetValue<bool>()
                    ?? false;
                var reboot = item?["requiresReboot"]?.GetValue<bool>()
                    ?? item?["needsReboot"]?.GetValue<bool>()
                    ?? item?["reboot"]?.GetValue<bool>()
                    ?? false;
                var optimized = item?["optimized"]?.GetValue<bool>() ?? false;
                var current = item?["current"]?.GetValue<string>() ?? "";
                var isDefault = item?["default"]?.GetValue<bool>() ?? false;
                var group = item?["group"]?.GetValue<string>() ?? "";
                AppState.Items.Add(new OptimizationItem(id, name, desc, sideEffect, admin, reboot, optimized, current, isDefault, group));
            }
        }

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
            var req = (item.RequiresAdmin ? "管理员" : "普通用户") +
                      (item.RequiresReboot ? "，需重启" : "");
            sb.AppendLine($"      要求：{req}");
            sb.AppendLine();
        }
        sb.AppendLine("详细原始 JSON 不在此显示，可在备份/日志页查看。");
        OutputBox.Text = sb.ToString();
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.DetectJson is null)
        {
            DialogService.Info("提示", "请先在检测页运行一次检测。");
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
            DialogService.Info("游戏路径", "已将游戏路径复制到剪贴板。");
        }
        else
        {
            DialogService.Info("游戏路径", "当前没有可复制的游戏路径，请先运行检测。");
        }
    }

    private void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            Clipboard.SetText(OutputBox.Text);
            DialogService.Info("检测详情", "已将检测详情复制到剪贴板。");
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
                new CheckItemViewModel("体检", "待检测", GetStatusBrush(string.Empty))
            };
        }

        return checks.Select(node =>
        {
            var name = node?["name"]?.GetValue<string>() ?? "体检项";
            var status = node?["status"]?.GetValue<string>() ?? string.Empty;
            var message = node?["message"]?.GetValue<string>() ?? string.Empty;
            var summary = string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(message)
                ? "待检测"
                : $"● {status}  {message}".Trim();
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
