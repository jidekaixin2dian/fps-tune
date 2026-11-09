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

    // 预设集合与 CLI/控制台页同源：都走 catalog.presets（OptimizationCatalog），
    // 不再各自硬编码，避免同一预设在不同界面解析出不同集合。
    private static HashSet<string> SafeOnlyIds() =>
        new(OptimizationCatalog.ResolvePreset("safe-only"), StringComparer.Ordinal);

    private static HashSet<string> BalancedIds() =>
        new(OptimizationCatalog.ResolvePreset("balanced"), StringComparer.Ordinal);

    public ObservableCollection<OptimizationItemViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; private set; } = null!;

    public OptimizeView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => { LangService.Changed += RefreshWorkspaceLabels; RefreshWorkspaceLabels(); };
        Unloaded += (_, _) => LangService.Changed -= RefreshWorkspaceLabels;
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
    internal void SelectForReview(IEnumerable<string> ids)
    {
        if (_operationBusy) return;
        ReloadFromState();
        SetCustomMode();
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var item in Items) item.IsChecked = selected.Contains(item.Id);
        ConsentCheck.IsChecked = false;
        AllGroups.IsChecked = true;
        SearchBox.Clear();
        ScopeFilter.SelectedIndex = 2; // Show the actual recommendations, regardless of earlier filters.
        ItemsView.Refresh();
        UpdateFilterSummary();
        if (Items.FirstOrDefault(i => i.IsChecked) is { } first)
        {
            ItemList.SelectedItem = first;
            ItemList.ScrollIntoView(first);
            SetItemDetail(first);
        }
        else SetPlain(Str.T("Str.SelectAtLeastOneItem"));
    }
    private string _groupFilter = "";
    private void ItemToggle_Click(object sender, RoutedEventArgs e)
    {
        SetCustomMode();
        if (sender is CheckBox { DataContext: OptimizationItemViewModel vm })
            SetItemDetail(vm);
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OptimizationItemViewModel.IsChecked)) return;
        ConsentCheck.IsChecked = false;
        if (_scopeFilter == "selected") { ItemsView.Refresh(); UpdateFilterSummary(); }
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        // 0.2.0 M1：勾选集合同步给 A/B 页自定义实测。
        AppState.SelectedIds.Clear();
        foreach (var id in Items.Where(i => i.IsChecked).Select(i => i.Id))
            AppState.SelectedIds.Add(id);
        var selected = Items.Where(i => i.IsChecked).ToList();
        if (!_operationBusy) ApplyButton.IsEnabled = selected.Count > 0;
        ClearSelectionButton.IsEnabled = selected.Count > 0;
        SelectionSummary.Text = Str.T("Str.OptSelectionSummary", selected.Count, selected.Count(i => i.Optimized),
            selected.Count(i => i.RequiresAdmin), selected.Count(i => i.RequiresReboot));
        var hidden = selected.Count(i => !ItemsView.Contains(i));
        if (hidden > 0) SelectionSummary.Text += "\n" + Str.T("Str.OptHiddenSelection", hidden);
    }

    private void UpdateFilterSummary()
    {
        if (ItemsView is null) return;
        var count = Items.Count(ItemsView.Contains);
        ItemListTitle.Text = Str.T("Str.OptFilteredCount", count, Items.Count);
        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0)
        {
            // 列表本身就是空的时候，用户不是"筛掉了"，而是还没把检测结果加载进来——
            // 这时候提示"调整搜索或分类"只会让人以为软件坏了。
            EmptyHint.Text = Items.Count == 0 ? Str.T("Str.NoItemsRunDetect") : Str.T("Str.NoMatchingItems");
        }
        UpdateSelectionSummary();
    }
    private bool FilterItem(object obj)
    {
        if (obj is not OptimizationItemViewModel vm)
            return false;
        if (!MatchesScope(vm, _scopeFilter)) return false;
        if (_groupFilter.Length > 0 && vm.Group != _groupFilter)
            return false;
        if (_searchText.Length == 0)
            return true;
        return vm.MatchesQuery(_searchText);
    }

    private void GroupChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string g)
        {
            _groupFilter = g;
            ItemsView?.Refresh();
            UpdateFilterSummary();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        ItemsView?.Refresh();
        UpdateFilterSummary();
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

    private List<OptimizationItem>? _sourceItems;
    private IReadOnlyList<VerdictStore.VerdictEntry> _verdicts = [];

    public void ReloadFromState()
    {
        if (_operationBusy) return;
        ReloadItemsFromState();
    }

    private void ReloadItemsFromState()
    {
        _verdicts = VerdictStore.Load();
        var currentGame = System.IO.Path.GetFileNameWithoutExtension(AppState.GamePath ?? "");
        // 页面切换不重建列表，也不清空用户尚未应用的自定义选择。
        if (ReferenceEquals(_sourceItems, AppState.Items))
        {
            foreach (var vm in Items)
                vm.VerdictBadge = VerdictStore.BadgeFor(vm.Id, _verdicts, currentGame, AppState.GamePath);
            return;
        }
        var selected = Items.Where(i => i.IsChecked).Select(i => i.Id).ToHashSet();
        var focusedId = (ItemList.SelectedItem as OptimizationItemViewModel)?.Id;
        // 分组 ListCollectionView 在集合变化时会访问 CurrentPosition，不能在
        // DeferRefresh 作用域内逐项添加。列表规模很小，且仅在快照变化时重建。
        foreach (var old in Items) old.PropertyChanged -= SelectionChanged;
        Items.Clear();
        foreach (var item in AppState.Items)
        {
            var vm = new OptimizationItemViewModel(item)
            {
                IsChecked = selected.Contains(item.Id),
                VerdictBadge = VerdictStore.BadgeFor(item.Id, _verdicts, currentGame, AppState.GamePath),
            };
            vm.PropertyChanged += SelectionChanged;
            Items.Add(vm);
        }
        UpdateFilterSummary();
        UpdateSelectionSummary();
        _sourceItems = AppState.Items;
        _loadedFromState = AppState.Items.Count > 0;
        if (!_loadedFromState)
        {
            SetPlain(Str.T("Str.NoItemsRunDetect"));
            return;
        }

        ShowPresetContents();
        if (Items.FirstOrDefault(i => i.Id == focusedId) is { } focused)
            ItemList.SelectedItem = focused;
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
            foreach (var item in Items.Where(i => SafeOnlyIds().Contains(i.Id)))
                item.IsChecked = true;
        }
        else
        {
            foreach (var item in Items.Where(i => BalancedIds().Contains(i.Id)))
                item.IsChecked = true;
        }
    }

    private void ShowPresetContents()
    {
        if (AppState.Items.Count == 0)
        {
            SetPlain(Str.T("Str.NoItemsRunDetect"));
            return;
        }

        if (PresetCustom.IsChecked != true)
            ApplyPresetChecks();

        if (PresetCustom.IsChecked == true)
        {
            SetPlain(Str.T("Str.OptCustomPaneIntro"));
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
            ids = SafeOnlyIds().ToList();
        }
        else
        {
            presetName = "balanced";
            ids = BalancedIds().ToList();
        }

        var selected = AppState.Items.Where(i => ids.Contains(i.Id)).ToList();
        var label = presetName == "full" ? Str.T("Str.PresetFull") : presetName == "safe-only" ? Str.T("Str.PresetSafe") : Str.T("Str.PresetBalanced");
        SetPlain(Str.T("Str.OptPresetPaneIntro", label, selected.Count) + "\n\n" +
            Str.T("Str.ToggleHint2") + "\n\n" +
            Str.T("Str.ItemDetailHint"));
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
            return SafeOnlyIds();
        if (PresetCustom.IsChecked == true)
            return Items.Where(i => i.IsChecked).Select(i => i.Id);
        return BalancedIds();
    }

    private async void OneClickButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        if (ConsentCheck.IsChecked != true)
        {
            DialogService.Warning(Str.T("Str.Unconfirmed"), Str.T("Str.ConsentOneClick"));
            return;
        }

        if (OptimizationCatalog.ResolvePreset("balanced").Any(id =>
                AppState.Items.FirstOrDefault(i => i.Id == id)?.RequiresAdmin == true)
            && !AdminHelper.IsAdministrator())
        {
            var elevate = DialogService.Confirm(
                Str.T("Str.NeedsAdmin"),
                Str.T("Str.OneClickNeedsAdmin", Str.T("Str.ConfirmRestartAdmin")),
                danger: false);
            if (elevate)
                AdminHelper.RestartAsAdministrator();
            return;
        }

        var confirmed = DialogService.Confirm(
            Str.T("Str.OneClick"),
            Str.T("Str.OneClickSystemConfirm", string.Join("\n", OptimizationCatalog.ResolvePreset("balanced")
                .Select(id => ItemCatalog.All.First(i => i.Id == id).DisplayName)), Str.T("Str.ConfirmAllBackedUp")),
            confirmText: Str.T("Str.Start"));
        if (!confirmed)
            return;

        SetOperationBusy(true);
        SetPlain(Str.T("Str.RunningOneClick"), "AccentBrush");
        try
        {
            var result = await OneClickOptimizer.ApplyAsync();
            await CompleteOperationAsync(result);
        }
        catch (Exception ex)
        {
            SetRawMonospace(ex.ToString());
        }
        finally
        {
            SetOperationBusy(false);
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        => await ApplyReviewedAsync();

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        var scopes = await Task.Run(RecoveryCoordinator.Inventory);
        if (_operationBusy) return;
        var backupCount = scopes.Count;
        if (backupCount == 0)
        {
            DialogService.Info(Str.T("Str.RestoreConfirm"), Str.T("Str.NoRestorableBackups"));
            return;
        }
        // 与「应用」同款预检：待还原记录含电源/服务/HKLM 时需要管理员，
        // 否则非管理员用户会拿到逐项失败的结果。
        if (!AdminHelper.IsAdministrator() && scopes.Any(scope => scope.NeedsAdmin))
        {
            var elevate = DialogService.Confirm(
                Str.T("Str.NeedsAdmin"),
                Str.T("Str.RestoreNeedsAdminBody"),
                confirmText: Str.T("Str.RestartAsAdminShort"),
                danger: false);
            if (elevate)
                AdminHelper.RestartAsAdministrator();
            return;
        }
        if (!DialogService.Confirm(Str.T("Str.RestoreConfirm"),
                Str.T("Str.OptRestoreConfirmAll", backupCount),
                danger: true))
            return;

        SetOperationBusy(true);
        SetPlain(Str.T("Str.Restoring"), "AccentBrush");

        try
        {
            var result = await OptimizationEngine.RestoreAsync();
            await CompleteOperationAsync(result);
        }
        catch (Exception ex)
        {
            SetRawMonospace(ex.ToString());
        }
        finally
        {
            SetOperationBusy(false);
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
            r.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("FontCode");
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
                gp.Inlines.Add(R("— " + CatalogGroups.Display(item.Group) + " —", "TextMutedBrush", size: 13));
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
            Line(Str.T("Str.Description"), vm.Description);
        if (!string.IsNullOrWhiteSpace(vm.SideEffect))
            Line(Str.T("Str.SideEffect"), vm.SideEffect, "WarningBrush");
        if (!string.IsNullOrWhiteSpace(vm.Current))
            Line(Str.T("Str.Current"), vm.Current);
        Line(Str.T("Str.Status"), vm.StatusText, vm.Optimized ? "OkBrush" : "TextSecondaryBrush");
        var req = (vm.RequiresAdmin ? Str.T("Str.Admin") : Str.T("Str.NormalUser")) + (vm.RequiresReboot ? Str.T("Str.RequiresRebootSuffix") : "");
        Line(Str.T("Str.Requirement"), req);
    }

    /// <summary>一条优化项的执行结果（从引擎 JSON 里读出来的界面视图）。</summary>
    private sealed record ApplyRow(string Id, string Name, bool Ok, bool Skipped, string Message);

    /// <summary>
    /// 解析引擎结果。退出码不再决定渲染方式：只要有结构化结果就逐项展示——
    /// 部分项目失败是正常结果，不该让界面变成一次"整体失败"。
    /// </summary>
    private static bool TryReadApplyResult(RunResult result, out JsonNode? root, out List<ApplyRow> rows)
    {
        root = null;
        rows = [];
        if (string.IsNullOrWhiteSpace(result.Output))
            return false;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(result.Output);
            if (root?["results"] is not System.Text.Json.Nodes.JsonArray results)
            {
                root = null;
                return false;
            }
            foreach (var item in results)
                rows.Add(new ApplyRow(
                    item?["id"]?.GetValue<string>() ?? "",
                    item?["name"]?.GetValue<string>() ?? "",
                    item?["ok"]?.GetValue<bool>() ?? false,
                    item?["skipped"]?.GetValue<bool>() ?? false,
                    item?["message"]?.GetValue<string>() ?? ""));
            return true;
        }
        catch
        {
            root = null;
            return false;
        }
    }

    private void SetApplyResult(RunResult result)
    {
        if (!TryReadApplyResult(result, out var root, out var rows))
        {
            ResetDoc();
            var failedHead = NewPara(6);
            failedHead.Inlines.Add(R(Str.T("Str.ExecFailedSp"), "DangerBrush", bold: true, size: 14));
            OutputDoc.Blocks.Add(failedHead);
            var note = NewPara(4);
            note.Inlines.Add(R(Str.T("Str.UnstructuredResult"), "TextMutedBrush"));
            OutputDoc.Blocks.Add(note);
            SetRawMonospace(string.Concat(
                string.IsNullOrWhiteSpace(result.Output) ? "" : result.Output + "\n",
                string.IsNullOrWhiteSpace(result.Error) ? "" : result.Error));
            return;
        }

        var applied = rows.Count(r => r.Ok && !r.Skipped);
        var failed = rows.Count(r => !r.Ok && !r.Skipped);
        var skipped = rows.Count(r => r.Skipped);

        ResetDoc();
        var head = NewPara(8);
        if (failed == 0)
        {
            head.Inlines.Add(R(Str.T("Str.ApplyDone"), "AccentBrush", bold: true, size: 15));
        }
        else
        {
            // 失败里有几项、界面先给结论，再把逐项原因摆在下面。
            head.Inlines.Add(R(Str.T(applied > 0 ? "Str.ApplyPartialHead" : "Str.ApplyAllFailedHead"),
                applied > 0 ? "WarningBrush" : "DangerBrush", bold: true, size: 15));
            head.Inlines.Add(R("   " + Str.T("Str.OptApplyCounts", applied, failed, skipped), "TextSecondaryBrush"));
        }
        OutputDoc.Blocks.Add(head);

        string? lastGroup = null;
        foreach (var row in rows)
        {
            var meta = ItemCatalog.All.FirstOrDefault(x => x.Id == row.Id);
            if (meta?.Group != lastGroup)
            {
                lastGroup = meta?.Group;
                var gp = NewPara(3);
                gp.Inlines.Add(R("— " + CatalogGroups.Display(lastGroup ?? Str.T("Str.Other")) + " —", "TextMutedBrush", size: 13));
                OutputDoc.Blocks.Add(gp);
            }

            var line = NewPara(3);
            line.Inlines.Add(R(row.Skipped ? Str.T("Str.Skipped") + " " : row.Ok ? Str.T("Str.SucceededSp") : Str.T("Str.FailedSp"),
                row.Skipped ? "TextMutedBrush" : row.Ok ? "OkBrush" : "DangerBrush", bold: true));
            line.Inlines.Add(R(row.Id, "AccentBrush", mono: true));
            line.Inlines.Add(R("  " + row.Name, "TextPrimaryBrush"));
            if (!string.IsNullOrWhiteSpace(row.Message))
                line.Inlines.Add(R("   " + row.Message, "TextMutedBrush"));
            OutputDoc.Blocks.Add(line);
        }

        // 顶部已经给了成功/失败/跳过的数字时不重复一遍汇总；只有全成功时才需要这行。
        var summary = root?["summary"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(summary) && failed == 0)
        {
            var sp = NewPara(4);
            sp.Inlines.Add(R(Str.T("Str.SummarySp"), "TextMutedBrush"));
            sp.Inlines.Add(R(summary, "TextSecondaryBrush"));
            OutputDoc.Blocks.Add(sp);
        }

        // 失败项需要给人下一步：需要管理员的项单独点名，别让用户自己从名称里猜。
        // 判定同时看目录里的 Admin 标记与逐项原因——目录标记漏标的项（系统拒绝写入）也要被点名。
        if (failed > 0)
        {
            var adminRows = rows.Where(r => !r.Ok && !r.Skipped
                    && (ItemCatalog.All.FirstOrDefault(x => x.Id == r.Id) is { Admin: true }
                        || r.Message == Str.T("Str.OptFailNeedAdmin")))
                .Select(r => r.Name).ToArray();
            var next = NewPara(4);
            next.Inlines.Add(R(adminRows.Length > 0
                ? Str.T("Str.OptApplyNextAdmin", adminRows.Length, string.Join(Str.T("Str.ListSeparator"), adminRows))
                : Str.T("Str.OptApplyNextRetry"), adminRows.Length > 0 ? "WarningBrush" : "TextSecondaryBrush"));
            OutputDoc.Blocks.Add(next);
        }

        var backup = root?["backupFile"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(backup))
        {
            var bp = NewPara(4);
            bp.Inlines.Add(R(Str.T("Str.BackupSp"), "TextMutedBrush"));
            bp.Inlines.Add(R(backup, "TextSecondaryBrush", mono: true, size: 13));
            OutputDoc.Blocks.Add(bp);
        }

        if (root?["reboot"] is System.Text.Json.Nodes.JsonArray reboot && reboot.Count > 0)
        {
            var ids = reboot.Select(r2 => r2?.GetValue<string>() ?? "").Where(x => !string.IsNullOrWhiteSpace(x));
            var rp = NewPara(0);
            rp.Inlines.Add(R(Str.T("Str.NeedsRebootSp"), "WarningBrush", bold: true));
            rp.Inlines.Add(R(string.Join(Str.T("Str.ListSeparator"), ids), "WarningBrush"));
            OutputDoc.Blocks.Add(rp);
        }

        // 最小化在托盘时也第一时间知道执行结果（部分失败同样要通知，否则只看到"改了"却不知道没全成）。
        TrayService.NotifyComplete(
            Str.T("Str.AppNameDone"),
            failed > 0
                ? Str.T("Str.OptApplyCounts", applied, failed, skipped)
                : string.IsNullOrWhiteSpace(summary) ? Str.T("Str.OptimizeDone") : summary);
    }

    private void SetRawMonospace(string text)
    {
        var para = NewPara(0);
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                para.Inlines.Add(new LineBreak());
            para.Inlines.Add(R(lines[i], "TextSecondaryBrush", mono: true, size: 13));
        }
        OutputDoc.Blocks.Add(para);
    }

    // ---------- 配置方案 ----------

    /// <summary>
    /// 打开「我的方案」弹窗（列表式管理：载入/导出/删除行内完成，顶部保存当前勾选）。
    /// 取代原先右栏 Expander 里的名称输入 + 五个小按钮——旧交互必须手输名字才能载入/删除。
    /// </summary>
    private void ProfilesOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileManagerWindow(
            () => Items.Where(i => i.IsChecked).Select(i => i.Id).ToList())
        { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (dlg.LoadRequestedIds is not null)
            ApplyProfileIds(dlg.LoadRequestedIds);
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
        SetItemListDoc(Str.T("Str.ProfileLoaded"), Str.T("Str.ItemsCountFmt", idSet.Count), selected);
    }
}

