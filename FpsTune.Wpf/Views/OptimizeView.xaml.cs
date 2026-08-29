using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using System.Linq;

namespace FpsTune.Wpf.Views;

public partial class OptimizeView : UserControl
{
    private bool _loadedFromState;

    private static readonly string[] SafeOnlyIds =
        { "game-mode", "dvr-off", "transparency-off", "fso-off", "gpu-pref" };

    private static readonly string[] BalancedExclude =
        { "sysmain-off", "wsearch-off", "hibernate-off", "power-tuning" };

    public ObservableCollection<OptimizationItemViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; private set; } = null!;

    public OptimizeView()
    {
        InitializeComponent();
        DataContext = this;
        PresetFull.Checked += (_, _) => { if (!_suppressPresetAutoCheck) ShowPresetContents(); };
        PresetBalanced.Checked += (_, _) => { if (!_suppressPresetAutoCheck) ShowPresetContents(); };
        PresetSafeOnly.Checked += (_, _) => { if (!_suppressPresetAutoCheck) ShowPresetContents(); };
        PresetCustom.Checked += (_, _) => { if (!_suppressPresetAutoCheck) ShowPresetContents(); };
        ItemList.SelectionChanged += (_, _) => ShowSelectedItem();
        ItemsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
    }

    private string _searchText = "";
    private bool FilterItem(object obj)
    {
        if (obj is not OptimizationItemViewModel vm)
            return false;
        if (_searchText.Length == 0)
            return true;
        return vm.Id.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || vm.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || vm.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        ItemsView.Refresh();
        ItemListTitle.Text = _searchText.Length == 0
            ? "优化项"
            : $"优化项（匹配 {Items.Count}/{Items.Count} 中的可见项）";
    }

    private void SelectAllVisible_Click(object sender, RoutedEventArgs e)
    {
        SetCustomMode();
        foreach (var vm in Items)
            if (ItemsView.Contains(vm))
                vm.IsChecked = true;
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        SetCustomMode();
        foreach (var vm in Items)
            if (ItemsView.Contains(vm))
                vm.IsChecked = false;
    }

    private void SetCustomMode()
    {
        _suppressPresetAutoCheck = true;
        PresetCustom.IsChecked = true;
        _suppressPresetAutoCheck = false;
        ShowPresetContents();
    }
    private bool _suppressPresetAutoCheck;

    public void ReloadFromState()
    {
        Items.Clear();
        foreach (var item in AppState.Items)
            Items.Add(new OptimizationItemViewModel(item));
        _loadedFromState = AppState.Items.Count > 0;
        ItemsView.Refresh();
        if (!_loadedFromState)
        {
            OutputBox.Text = "暂无优化项，请先在检测页运行检测。";
            return;
        }

        ShowPresetContents();
    }


    private void ApplyPresetChecks()
    {
        foreach (var item in Items)
            item.IsChecked = false;

        if (PresetFull.IsChecked == true)
        {
            foreach (var item in Items)
                item.IsChecked = true;
        }
        else if (PresetSafeOnly.IsChecked == true)
        {
            foreach (var item in Items.Where(i => SafeOnlyIds.Contains(i.Id)))
                item.IsChecked = true;
        }
        else
        {
            foreach (var item in Items.Where(i => !BalancedExclude.Contains(i.Id)))
                item.IsChecked = true;
        }
    }

    private void ShowPresetContents()
    {
        if (AppState.Items.Count == 0)
        {
            OutputBox.Text = "暂无优化项，请先在检测页运行检测。";
            return;
        }

        var sb = new StringBuilder();
        if (PresetCustom.IsChecked != true)
            ApplyPresetChecks();

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

    private IEnumerable<string> CollectSelectedIds()
    {
        if (PresetFull.IsChecked == true)
            return AppState.Items.Select(i => i.Id);
        if (PresetSafeOnly.IsChecked == true)
            return SafeOnlyIds;
        if (PresetCustom.IsChecked == true)
            return Items.Where(i => i.IsChecked).Select(i => i.Id);
        return AppState.Items.Select(i => i.Id).Where(id => !BalancedExclude.Contains(id));
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsentCheck.IsChecked != true)
        {
            DialogService.Warning("未确认", "请先勾选同意说明，再执行应用。");
            return;
        }

        // 所选项包含需要管理员的项而当前非管理员时，先提供提权重启，避免一批项直接失败。
        var selectedIds = CollectSelectedIds().ToList();
        if (selectedIds.Count > 0
            && AppState.Items.Any(i => selectedIds.Contains(i.Id) && i.RequiresAdmin)
            && !AdminHelper.IsAdministrator())
        {
            var elevate = DialogService.Confirm(
                "需要管理员权限",
                "所选优化项中包含需要管理员权限的项目，而当前程序不是以管理员身份运行的。\n\n" +
                "要以管理员身份重启并继续应用吗？\n\n" +
                "如果只想修改无需管理员的项目，可以切换到 safe-only 预设。",
                danger: false);
            if (elevate)
                AdminHelper.RestartAsAdministrator();
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
                DialogService.Info("提示", "请勾选至少一个优化项。");
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
            RunResult result;
            if (PresetCustom.IsChecked == true)
            {
                var ids = Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
                result = await OptimizationEngine.ApplyItemsAsync(ids);
            }
            else
            {
                var preset = PresetFull.IsChecked == true ? "full"
                    : PresetSafeOnly.IsChecked == true ? "safe-only" : "balanced";
                result = await OptimizationEngine.ApplyPresetAsync(preset);
            }
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
        var backupCount = BackupService.ListBackups().Count;
        if (backupCount == 0)
        {
            DialogService.Info("还原确认", "当前没有可还原的备份。");
            return;
        }
        if (!DialogService.Confirm("还原确认",
                $"共找到 {backupCount} 个备份文件，将把它们记录的全部系统改动逐项恢复为原值。\n\n确定继续吗？",
                danger: true))
            return;

        RestoreButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        OutputBox.Text = "正在还原...";

        try
        {
            var result = await OptimizationEngine.RestoreAsync();
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
        var error = new StringBuilder();
        if (!result.Success)
        {
            error.AppendLine($"exit={result.ExitCode}");
            if (!string.IsNullOrWhiteSpace(result.Output))
                error.AppendLine("--- STDOUT ---").AppendLine(result.Output);
            if (!string.IsNullOrWhiteSpace(result.Error))
                error.AppendLine("--- STDERR ---").AppendLine(result.Error);
            return error.ToString();
        }

        // 尝试把 Apply 结果解析成逐项清单
        try
        {
            var root = JsonNode.Parse(result.Output);
            if (root?["results"] is JsonArray results)
            {
                var sb = new StringBuilder();
                sb.AppendLine("== 应用结果 ==");
                foreach (var item in results)
                {
                    var id = item?["id"]?.GetValue<string>() ?? "";
                    var name = item?["name"]?.GetValue<string>() ?? "";
                    var ok = item?["ok"]?.GetValue<bool>() ?? false;
                    var skipped = item?["skipped"]?.GetValue<bool>() ?? false;
                    var message = item?["message"]?.GetValue<string>() ?? "";
                    var tag = skipped ? "[跳过]" : ok ? "[成功]" : "[失败]";
                    sb.AppendLine($"{tag} {id}  {name}  {message}");
                }

                var summary = root["summary"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    sb.AppendLine();
                    sb.AppendLine("汇总：" + summary);
                }

                var backup = root["backupFile"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(backup))
                    sb.AppendLine("备份：" + backup);

                if (root["reboot"] is JsonArray reboot && reboot.Count > 0)
                {
                    var ids = reboot.Select(r => r?.GetValue<string>() ?? "").Where(x => !string.IsNullOrWhiteSpace(x));
                    sb.AppendLine("需重启：" + string.Join(", ", ids));
                }
                return sb.ToString();
            }
        }
        catch
        {
            // 非 JSON 或非 Apply 结果，继续走原始输出
        }

        var raw = new StringBuilder();
        raw.AppendLine(result.Output);
        if (!string.IsNullOrWhiteSpace(result.Error))
            raw.AppendLine("--- STDERR ---").AppendLine(result.Error);
        return raw.ToString();
    }
}
