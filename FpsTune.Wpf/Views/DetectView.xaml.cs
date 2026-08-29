using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using System.Linq;

namespace FpsTune.Wpf.Views;

public partial class DetectView : UserControl
{
    private bool _hasSavedState;

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
        };
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
        => _ = await RunDetectionAsync();

    public Task<bool> RunOnboardingDetectionAsync()
        => RunDetectionAsync();

    private async Task<bool> RunDetectionAsync()
    {
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
        if (checks is not null)
        {
            SetCheck(Check1Text, checks.ElementAtOrDefault(0));
            SetCheck(Check2Text, checks.ElementAtOrDefault(1));
            SetCheck(Check3Text, checks.ElementAtOrDefault(2));
        }

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

    private static void SetCheck(System.Windows.Controls.TextBlock target, JsonNode? node)
    {
        if (node is null)
        {
            target.Text = "待检测";
            return;
        }

        var status = node["status"]?.GetValue<string>() ?? "";
        var message = node["message"]?.GetValue<string>() ?? "";
        target.Text = $"● {status}  {message}".Trim();
        target.Foreground = status switch
        {
            "ok" => (System.Windows.Media.Brush)Application.Current.Resources["OkBrush"],
            "attention" => (System.Windows.Media.Brush)Application.Current.Resources["WarningBrush"],
            "danger" => (System.Windows.Media.Brush)Application.Current.Resources["DangerBrush"],
            _ => (System.Windows.Media.Brush)Application.Current.Resources["TextSecondaryBrush"],
        };
    }
}
