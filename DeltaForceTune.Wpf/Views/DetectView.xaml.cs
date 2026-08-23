using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class DetectView : UserControl
{
    private readonly string _enginePath;

    public DetectView()
    {
        InitializeComponent();
        _enginePath = ScriptLocator.Resolve("delta-optimizer.ps1");
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        OutputBox.Text = "正在检测...";

        try
        {
            var result = await PowerShellRunner.RunAsync(_enginePath, "-Detect", "-Json");
            if (!result.Success)
            {
                OutputBox.Text = result.Error + Environment.NewLine + result.Output;
                return;
            }

            var root = JsonNode.Parse(result.Output)?.AsObject();
            if (root is null)
            {
                OutputBox.Text = "无法解析检测 JSON。";
                return;
            }

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
                    var desc = item?["description"]?.GetValue<string>() ?? "";
                    var admin = item?["requiresAdmin"]?.GetValue<bool>()
                        ?? item?["needsAdmin"]?.GetValue<bool>()
                        ?? item?["admin"]?.GetValue<bool>()
                        ?? false;
                    var reboot = item?["requiresReboot"]?.GetValue<bool>()
                        ?? item?["needsReboot"]?.GetValue<bool>()
                        ?? item?["reboot"]?.GetValue<bool>()
                        ?? false;
                    AppState.Items.Add(new OptimizationItem(id, desc, admin, reboot));
                }
            }

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
            sb.AppendLine($"优化项共 {AppState.Items.Count} 项。");
            sb.AppendLine();
            sb.AppendLine(result.Output);
            OutputBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.ToString();
        }
        finally
        {
            RunButton.IsEnabled = true;
            LoadButton.IsEnabled = true;
        }
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.DetectJson is null)
        {
            MessageBox.Show("请先在检测页运行一次检测。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = Window.GetWindow(this);
        if (window is MainWindow main)
            main.ShowOptimizePage();
        MessageBox.Show($"已加载 {AppState.Items.Count} 个优化项到优化页。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void SetCheck(System.Windows.Controls.TextBlock target, JsonNode? node)
    {
        if (node is null)
        {
            target.Text = "--";
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
