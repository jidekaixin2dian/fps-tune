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

    private static readonly string[] SafeOnlyIds =
        { "game-mode", "dvr-off", "transparency-off", "fso-off", "gpu-pref" };

    private static readonly string[] BalancedExclude =
        { "sysmain-off", "wsearch-off", "hibernate-off", "power-tuning" };

    public ObservableCollection<OptimizationItemViewModel> Items { get; } = new();

    public OptimizeView()
    {
        InitializeComponent();
        DataContext = this;
        _enginePath = ScriptLocator.Resolve("delta-optimizer.ps1");

        PresetFull.Checked += (_, _) => ShowPresetContents();
        PresetBalanced.Checked += (_, _) => ShowPresetContents();
        PresetSafeOnly.Checked += (_, _) => ShowPresetContents();
        PresetCustom.Checked += (_, _) => ShowPresetContents();
        ItemList.SelectionChanged += (_, _) => ShowSelectedItem();
    }

    public void ReloadFromState()
    {
        Items.Clear();
        foreach (var item in AppState.Items)
            Items.Add(new OptimizationItemViewModel(item));
        _loadedFromState = AppState.Items.Count > 0;
        if (!_loadedFromState)
        {
            OutputBox.Text = "暂无优化项，请先在检测页运行检测。";
            return;
        }

        ShowPresetContents();
    }


    private void ShowPresetContents()
    {
        if (AppState.Items.Count == 0)
        {
            OutputBox.Text = "暂无优化项，请先在检测页运行检测。";
            return;
        }

        var sb = new StringBuilder();
        if (PresetCustom.IsChecked == true)
        {
            sb.AppendLine("== 自定义模式 ==");
            sb.AppendLine("请在左侧列表中勾选需要执行的优化项，然后点击“应用”。");
            OutputBox.Text = sb.ToString();
            return;
        }

        var presetName = PresetFull.IsChecked == true ? "full" : "balanced";
        List<string> ids;
        if (PresetFull.IsChecked == true)
        {
            presetName = "full";
            ids = AppState.Items.Select(i => i.Id).ToList();
        }
        else if (PresetSafeOnly.IsChecked == true)
        {
            presetName = "safe-only";
            ids = SafeOnlyIds.ToList();
        }
        else
        {
            presetName = "balanced";
            ids = AppState.Items
                .Where(i => !BalancedExclude.Contains(i.Id))
                .Select(i => i.Id)
                .ToList();
        }

        var selected = AppState.Items.Where(i => ids.Contains(i.Id)).ToList();
        sb.AppendLine($"== 预设 {presetName} 包含 {selected.Count} 项 ==");
        foreach (var item in selected)
            sb.AppendLine($"{item.Id}  {item.Name}");
        OutputBox.Text = sb.ToString();
    }

    private void ShowSelectedItem()
    {
        if (ItemList.SelectedItem is not OptimizationItemViewModel vm)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"== {vm.Id}  {vm.Name} ==");
        if (!string.IsNullOrWhiteSpace(vm.Description))
            sb.AppendLine($"说明：{vm.Description}");
        if (!string.IsNullOrWhiteSpace(vm.SideEffect))
            sb.AppendLine($"副作用：{vm.SideEffect}");
        if (!string.IsNullOrWhiteSpace(vm.Current))
            sb.AppendLine($"当前：{vm.Current}");
        sb.AppendLine($"状态：{vm.StatusText}");
        var req = (vm.RequiresAdmin ? "管理员" : "普通用户") +
                  (vm.RequiresReboot ? "，需重启" : "");
        sb.AppendLine($"要求：{req}");
        OutputBox.Text = sb.ToString();
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
