using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
        ItemsView.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(OptimizationItemViewModel.Group)));
    }

    private string _searchText = "";
    private string _groupFilter = "";
    private bool FilterItem(object obj)
    {
        if (obj is not OptimizationItemViewModel vm)
            return false;
        if (_groupFilter.Length > 0 && vm.Group != _groupFilter)
            return false;
        if (_searchText.Length == 0)
            return true;
        return vm.Id.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || vm.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || vm.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void GroupChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string g)
        {
            _groupFilter = g;
            ItemsView.Refresh();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        ItemsView.Refresh();
        if (_searchText.Length == 0)
        {
            ItemListTitle.Text = "优化项";
        }
        else
        {
            var visible = Items.Cast<OptimizationItemViewModel>().Count(ItemsView.Contains);
            ItemListTitle.Text = $"优化项（{visible}/{Items.Count}）";
        }
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
            SetPlain("暂无优化项，请先在检测页运行检测。");
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
            SetPlain("暂无优化项，请先在检测页运行检测。");
            return;
        }

        if (PresetCustom.IsChecked != true)
            ApplyPresetChecks();

        if (PresetCustom.IsChecked == true)
        {
            SetPlain("自定义模式：请在左侧列表中勾选需要执行的优化项，然后点击“应用”。");
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
        SetItemListDoc("预设", $"{presetName} · {selected.Count} 项", selected);
    }

    private void ShowSelectedItem()
    {
        if (ItemList.SelectedItem is not OptimizationItemViewModel vm)
            return;

        SetItemDetail(vm);
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

        if (PresetCustom.IsChecked == true && Items.All(i => !i.IsChecked))
        {
            DialogService.Info("提示", "请勾选至少一个优化项。");
            return;
        }

        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        SetPlain("正在应用...", "AccentBrush");

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
            SetApplyResult(result);
        }
        catch (Exception ex)
        {
            SetRawMonospace(ex.ToString());
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
        SetPlain("正在还原...", "AccentBrush");

        try
        {
            var result = await OptimizationEngine.RestoreAsync();
            SetApplyResult(result);
        }
        catch (Exception ex)
        {
            SetRawMonospace(ex.ToString());
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            RestoreButton.IsEnabled = true;
        }
    }



    // ---------- 执行结果富文本 ----------

    private void ResetDoc()
    {
        OutputDoc.Blocks.Clear();
        OutputDoc.Background = System.Windows.Media.Brushes.Transparent;
    }

    private static Paragraph NewPara(double bottom = 5)
        => new Paragraph { Margin = new Thickness(0, 0, 0, bottom) };

    private static Run R(string text, string brushKey, bool bold = false, double? size = null, bool mono = false)
    {
        var r = new Run(text);
        if (Application.Current.Resources[brushKey] is Brush b)
            r.Foreground = b;
        if (bold)
            r.FontWeight = FontWeights.SemiBold;
        if (size is not null)
            r.FontSize = size.Value;
        if (mono)
            r.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        return r;
    }

    private void SetPlain(string text, string brushKey = "TextSecondaryBrush", bool mono = false)
    {
        ResetDoc();
        var para = NewPara(0);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                para.Inlines.Add(new LineBreak());
            para.Inlines.Add(R(lines[i], brushKey, mono: mono));
        }
        OutputDoc.Blocks.Add(para);
    }

    /// <summary>预设/方案清单：分组小标题 + 等宽 id + 名称。</summary>
    private void SetItemListDoc(string title, string accentLabel, System.Collections.Generic.IEnumerable<OptimizationItem> items)
    {
        ResetDoc();
        var head = NewPara(8);
        head.Inlines.Add(R(title + " ", "TextSecondaryBrush"));
        head.Inlines.Add(R(accentLabel, "AccentBrush", bold: true, size: 15));
        OutputDoc.Blocks.Add(head);

        string? lastGroup = null;
        foreach (var item in items)
        {
            if (item.Group != lastGroup)
            {
                lastGroup = item.Group;
                var gp = NewPara(3);
                gp.Inlines.Add(R("— " + item.Group + " —", "TextMutedBrush", size: 11));
                OutputDoc.Blocks.Add(gp);
            }
            var line = NewPara(3);
            line.Inlines.Add(R(item.Id, "AccentBrush", mono: true));
            line.Inlines.Add(R("   " + item.Name, "TextPrimaryBrush"));
            OutputDoc.Blocks.Add(line);
        }
    }

    private void SetItemDetail(OptimizationItemViewModel vm)
    {
        ResetDoc();
        var head = NewPara(8);
        head.Inlines.Add(R(vm.Id, "AccentBrush", mono: true, size: 13));
        head.Inlines.Add(R("   " + vm.Name, "TextPrimaryBrush", bold: true, size: 14));
        OutputDoc.Blocks.Add(head);

        void Line(string label, string value, string valueBrush = "TextSecondaryBrush")
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var p = NewPara(3);
            p.Inlines.Add(R(label + "  ", "TextMutedBrush"));
            p.Inlines.Add(R(value, valueBrush));
            OutputDoc.Blocks.Add(p);
        }

        if (!string.IsNullOrWhiteSpace(vm.Description))
            Line("说明", vm.Description);
        if (!string.IsNullOrWhiteSpace(vm.SideEffect))
            Line("副作用", vm.SideEffect, "WarningBrush");
        if (!string.IsNullOrWhiteSpace(vm.Current))
            Line("当前", vm.Current);
        Line("状态", vm.StatusText, vm.Optimized ? "OkBrush" : "TextSecondaryBrush");
        var req = (vm.RequiresAdmin ? "管理员" : "普通用户") + (vm.RequiresReboot ? "，需重启" : "");
        Line("要求", req);
    }

    private void SetApplyResult(RunResult result)
    {
        if (!result.Success)
        {
            ResetDoc();
            var head = NewPara(6);
            head.Inlines.Add(R("执行失败 ", "DangerBrush", bold: true, size: 14));
            head.Inlines.Add(R($"exit={result.ExitCode}", "TextMutedBrush", mono: true));
            OutputDoc.Blocks.Add(head);
            SetRawMonospace(string.Concat(
                string.IsNullOrWhiteSpace(result.Output) ? "" : result.Output + "\n",
                string.IsNullOrWhiteSpace(result.Error) ? "" : result.Error));
            return;
        }

        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(result.Output);
            if (root?["results"] is not System.Text.Json.Nodes.JsonArray results)
                throw new InvalidOperationException("非结构化结果");

            ResetDoc();
            var head = NewPara(8);
            head.Inlines.Add(R("应用完成", "AccentBrush", bold: true, size: 15));
            OutputDoc.Blocks.Add(head);

            string? lastGroup = null;
            foreach (var item in results)
            {
                var id = item?["id"]?.GetValue<string>() ?? "";
                var name = item?["name"]?.GetValue<string>() ?? "";
                var ok = item?["ok"]?.GetValue<bool>() ?? false;
                var skipped = item?["skipped"]?.GetValue<bool>() ?? false;
                var message = item?["message"]?.GetValue<string>() ?? "";

                var meta = ItemCatalog.All.FirstOrDefault(x => x.Id == id);
                if (meta?.Group != lastGroup)
                {
                    lastGroup = meta?.Group;
                    var gp = NewPara(3);
                    gp.Inlines.Add(R("— " + (lastGroup ?? "其他") + " —", "TextMutedBrush", size: 11));
                    OutputDoc.Blocks.Add(gp);
                }

                var line = NewPara(3);
                line.Inlines.Add(R(skipped ? "跳过 " : ok ? "成功 " : "失败 ",
                    skipped ? "TextMutedBrush" : ok ? "OkBrush" : "DangerBrush", bold: true));
                line.Inlines.Add(R(id, "AccentBrush", mono: true));
                line.Inlines.Add(R("  " + name, "TextPrimaryBrush"));
                if (!string.IsNullOrWhiteSpace(message))
                    line.Inlines.Add(R("   " + message, "TextMutedBrush"));
                OutputDoc.Blocks.Add(line);
            }

            var summary = root["summary"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                var sp = NewPara(4);
                sp.Inlines.Add(R("汇总  ", "TextMutedBrush"));
                sp.Inlines.Add(R(summary, "TextSecondaryBrush"));
                OutputDoc.Blocks.Add(sp);
            }

            var backup = root["backupFile"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(backup))
            {
                var bp = NewPara(4);
                bp.Inlines.Add(R("备份  ", "TextMutedBrush"));
                bp.Inlines.Add(R(backup, "TextSecondaryBrush", mono: true, size: 11));
                OutputDoc.Blocks.Add(bp);
            }

            if (root["reboot"] is System.Text.Json.Nodes.JsonArray reboot && reboot.Count > 0)
            {
                var ids = reboot.Select(r2 => r2?.GetValue<string>() ?? "").Where(x => !string.IsNullOrWhiteSpace(x));
                var rp = NewPara(0);
                rp.Inlines.Add(R("需重启  ", "WarningBrush", bold: true));
                rp.Inlines.Add(R(string.Join("、", ids), "WarningBrush"));
                OutputDoc.Blocks.Add(rp);
            }

            // 最小化在托盘时也第一时间知道执行结果
            TrayService.NotifyComplete(
                "FPS 帧律 · 执行完成",
                string.IsNullOrWhiteSpace(summary) ? "系统优化执行完成，改动已自动备份，可随时还原。" : summary);
        }
        catch
        {
            ResetDoc();
            SetRawMonospace(result.Output + "\n" + result.Error);
        }
    }

    private void SetRawMonospace(string text)
    {
        var para = NewPara(0);
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                para.Inlines.Add(new LineBreak());
            para.Inlines.Add(R(lines[i], "TextSecondaryBrush", mono: true, size: 11));
        }
        OutputDoc.Blocks.Add(para);
    }

    // ---------- 配置方案 ----------

    private string CurrentProfileName => ProfileNameBox.Text.Trim();

    private void SetProfileHint(string text)
    {
        ProfileHint.Text = text;
    }

    private void ApplyProfileIds(IEnumerable<string> ids)
    {
        _suppressPresetAutoCheck = true;
        PresetCustom.IsChecked = true;
        _suppressPresetAutoCheck = false;

        var idSet = ids.ToHashSet();
        foreach (var vm in Items)
            vm.IsChecked = idSet.Contains(vm.Id);

        var selected = AppState.Items.Where(i => idSet.Contains(i.Id)).ToList();
        SetItemListDoc("方案已载入", $"{idSet.Count} 项", selected);
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        if (name.Length == 0)
        {
            DialogService.Warning("配置方案", "请先在输入框填写方案名称。");
            return;
        }
        var ids = Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            DialogService.Warning("配置方案", "当前没有勾选任何优化项。");
            return;
        }

        var profiles = ProfileStore.Load();
        var existing = profiles.FirstOrDefault(p => p.Name == name);
        if (existing is not null
            && !DialogService.Confirm("配置方案", $"方案「{name}」已存在，覆盖？", danger: true))
            return;
        profiles.RemoveAll(p => p.Name == name);
        profiles.Add(new OptProfile(name, ids));
        ProfileStore.Save(profiles);
        SetProfileHint($"已保存「{name}」（{ids.Count} 项）");
    }

    private void ProfileLoad_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        var profiles = ProfileStore.Load();
        var hit = profiles.FirstOrDefault(p => p.Name == name);
        if (hit is null)
        {
            var names = profiles.Count == 0 ? "（尚无已保存方案）" : string.Join("、", profiles.Select(p => p.Name));
            DialogService.Warning("配置方案", $"未找到方案「{name}」。已有：{names}");
            return;
        }
        if (hit.Ids is null || hit.Ids.Count == 0)
        {
            DialogService.Warning("配置方案", $"方案「{name}」不包含任何优化项，可能是文件损坏，请删除后重建。");
            return;
        }
        ApplyProfileIds(hit.Ids);
        SetProfileHint($"已载入「{name}」（{hit.Ids.Count} 项）");
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        var profiles = ProfileStore.Load();
        if (profiles.All(p => p.Name != name))
        {
            DialogService.Warning("配置方案", $"未找到方案「{name}」。");
            return;
        }
        if (!DialogService.Confirm("配置方案", $"删除方案「{name}」？", danger: true))
            return;
        profiles.RemoveAll(p => p.Name == name);
        ProfileStore.Save(profiles);
        SetProfileHint($"已删除「{name}」");
    }

    private void ProfileExport_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        var hit = ProfileStore.Load().FirstOrDefault(p => p.Name == name);
        if (hit is null)
        {
            DialogService.Warning("配置方案", $"未找到方案「{name}」，无法导出。");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出配置方案",
            Filter = "FPS 帧律方案 (*.fpsprofile.json)|*.fpsprofile.json",
            FileName = name + ".fpsprofile.json"
        };
        if (dlg.ShowDialog() != true)
            return;
        System.IO.File.WriteAllText(dlg.FileName,
            System.Text.Json.JsonSerializer.Serialize(hit, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        SetProfileHint($"已导出到 {dlg.FileName}");
    }

    private void ProfileImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入配置方案",
            Filter = "FPS 帧律方案 (*.fpsprofile.json)|*.fpsprofile.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var hit = System.Text.Json.JsonSerializer.Deserialize<OptProfile>(
                System.IO.File.ReadAllText(dlg.FileName, Encoding.UTF8));
            if (hit is null || string.IsNullOrWhiteSpace(hit.Name))
                throw new InvalidOperationException("文件内容不是有效的配置方案");
            var profiles = ProfileStore.Load();
            profiles.RemoveAll(p => p.Name == hit.Name);
            profiles.Add(hit);
            ProfileStore.Save(profiles);
            ProfileNameBox.Text = hit.Name;
            ApplyProfileIds(hit.Ids);
            SetProfileHint($"已导入「{hit.Name}」（{hit.Ids.Count} 项）");
        }
        catch (Exception ex)
        {
            DialogService.Warning("配置方案", "导入失败：" + ex.Message);
        }
    }}
