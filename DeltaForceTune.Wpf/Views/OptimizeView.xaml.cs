using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class OptimizeView : UserControl
{
    private readonly string _enginePath;
    private bool _loadedFromState;

    public ObservableCollection<OptimizationItemViewModel> Items { get; } = new();

    public OptimizeView()
    {
        InitializeComponent();
        DataContext = this;
        _enginePath = ScriptLocator.Resolve("delta-optimizer.ps1");
    }

    public void ReloadFromState()
    {
        Items.Clear();
        foreach (var item in AppState.Items)
            Items.Add(new OptimizationItemViewModel(item));
        _loadedFromState = AppState.Items.Count > 0;
        if (!_loadedFromState)
            OutputBox.Text = "暂无优化项，请先在检测页运行检测。";
    }


    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsentCheck.IsChecked != true)
        {
            MessageBox.Show("请先勾选同意说明，再执行应用。", "未确认", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var args = new List<string>();
        if (PresetFull.IsChecked == true)
            args.AddRange(new[] { "-Apply", "-Preset", "full", "-Force", "-Json" });
        else if (PresetSafeOnly.IsChecked == true)
            args.AddRange(new[] { "-Apply", "-Preset", "safe-only", "-Force", "-Json" });
        else if (PresetCustom.IsChecked == true)
        {
            var ids = Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
            if (ids.Count == 0)
            {
                MessageBox.Show("请勾选至少一个优化项。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            args.AddRange(new[] { "-Apply", "-Items", string.Join(",", ids), "-Force", "-Json" });
        }
        else
        {
            args.AddRange(new[] { "-Apply", "-Preset", "balanced", "-Force", "-Json" });
        }

        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        OutputBox.Text = "正在应用...";

        try
        {
            var result = await PowerShellRunner.RunAsync(_enginePath, args.ToArray());
            OutputBox.Text = FormatResult(result);
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.ToString();
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            RestoreButton.IsEnabled = true;
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show("确定要还原全部已备份的项目吗？", "还原确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        RestoreButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        OutputBox.Text = "正在还原...";

        try
        {
            var result = await PowerShellRunner.RunAsync(_enginePath, "-Restore", "-Json");
            OutputBox.Text = FormatResult(result);
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.ToString();
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            RestoreButton.IsEnabled = true;
        }
    }

    private static string FormatResult(RunResult result)
    {
        if (result.Success)
        {
            var sb = new StringBuilder();
            sb.AppendLine(result.Output);
            if (!string.IsNullOrWhiteSpace(result.Error))
                sb.AppendLine("--- STDERR ---").AppendLine(result.Error);
            return sb.ToString();
        }

        var error = new StringBuilder();
        error.AppendLine($"exit={result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.Output))
            error.AppendLine("--- STDOUT ---").AppendLine(result.Output);
        if (!string.IsNullOrWhiteSpace(result.Error))
            error.AppendLine("--- STDERR ---").AppendLine(result.Error);
        return error.ToString();
    }
}
