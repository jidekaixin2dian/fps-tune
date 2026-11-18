using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class OptimizeView
{
    private bool _operationBusy;
    private string _scopeFilter = "all";
    internal Func<IReadOnlyList<string>, string?, Task<NativeOptimizationEngine.ApplyReceipt>>? ApplyItemsOverride { get; set; }
    internal Func<Task<bool>>? RefreshDetectionOverride { get; set; }

    /// <summary>对照一下（0.5.0 M2）：改前采样 → 审阅确认应用 → 冷却 → 改后采样 → 结论卡。
    /// 写入仍走既有审阅窗确认，本入口不绕过审阅。</summary>
    private void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        if (ComparisonPanel.IsRunning) return;
        if (ConsentCheck.IsChecked != true)
        {
            DialogService.Warning(Str.T("Str.ReviewTitle"), Str.T("Str.ConsentApply")); return;
        }
        var plan = CaptureReviewPlan();
        if (plan.Items.Count == 0)
        {
            DialogService.Info(Str.T("Str.ReviewTitle"), Str.T("Str.SelectAtLeastOneItem")); return;
        }
        // 管理员预检与审阅应用同口径：提权类项目在开始对照前就拦下，不让用户等完一次采样才知道跑不了。
        var adminItems = plan.Items.Where(i => i.RequiresAdmin).Select(i => i.Name).ToArray();
        if (adminItems.Length > 0 && !AdminHelper.IsAdministrator())
        {
            var elevate = DialogService.Confirm(Str.T("Str.NeedsAdmin"),
                Str.T("Str.ReviewElevationItems", adminItems.Length,
                    string.Join(Str.T("Str.ListSeparator"), adminItems)),
                confirmText: Str.T("Str.RestartAsAdminShort"), danger: false);
            if (elevate) AdminHelper.RestartAsAdministrator();
            return;
        }

        ComparisonPanel.Visibility = Visibility.Visible;
        SetOperationBusy(true);
        ComparisonPanel.Start(
            new ComparisonRequest(plan.Ids, plan.GamePath),
            MakeCompareApply(plan),
            onFinished: () => SetOperationBusy(false));
    }

    /// <summary>对照的应用回调：弹既有审阅窗确认后走 ApplyItemsWithReceiptAsync（含备份快照与逐项回执）。</summary>
    private Func<IReadOnlyList<string>, string?, CancellationToken, Task<ComparisonApplyOutcome>> MakeCompareApply(OptimizationApplyPlan plan) =>
        async (ids, gamePath, ct) =>
        {
            var confirmed = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var review = new OptimizationReviewWindow(plan)
                {
                    Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow
                };
                DialogService.PrepareWindowForTest?.Invoke(review);
                confirmed = review.ShowDialog() == true;
            });
            if (!confirmed)
                return new ComparisonApplyOutcome(false, Str.T("Str.ComparisonReviewCancelled"), null, []);
            ct.ThrowIfCancellationRequested();
            var receipt = await NativeOptimizationEngine.ApplyItemsWithReceiptAsync(ids, gamePath).ConfigureAwait(false);
            var changed = receipt.Results.Where(r => r.Changed || r.StateUncertain).Select(r => r.Id).ToList();
            var errors = receipt.Results.Where(r => !r.Ok && !r.Skipped).Select(r => r.Id + " — " + r.Message).ToList();
            return new ComparisonApplyOutcome(errors.Count == 0,
                errors.Count == 0 ? null : string.Join("; ", errors), receipt.BackupFile, changed);
        };

    private void DriverSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        var dialog = new DriverSettingsWindow { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        dialog.ShowDialog();
    }
    private void PowerDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        var dialog = new PowerDetailsWindow { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        dialog.ShowDialog();
    }
    private void RefreshWorkspaceLabels()
    {
        foreach (var item in Items) item.RefreshLanguage();
        ItemsView?.Refresh(); UpdateFilterSummary();
    }
    private void ScopeFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ScopeFilter?.SelectedItem is ComboBoxItem { Tag: string mode }) _scopeFilter = mode;
        if (ItemsView is not null)
        {
            ItemsView.GroupDescriptions.Clear();
            if (_scopeFilter != "selected") ItemsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(OptimizationItemViewModel.Group)));
        }
        ItemsView?.Refresh(); UpdateFilterSummary();
    }
    internal static bool MatchesScope(OptimizationItemViewModel item, string mode) => mode switch {
        "pending" => item.Available && !item.Optimized, "selected" => item.IsChecked,
        "noAdmin" => item.Available && !item.RequiresAdmin, "noRestart" => item.Available && !item.RequiresReboot,
        "effects" => item.HasSideEffect, _ => true
    };
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        SetCustomMode(); foreach (var item in Items) item.IsChecked = false;
    }
    internal void RevealItem(string id)
    {
        if (_operationBusy) return;
        ReloadFromState(); AllGroups.IsChecked = true; ScopeFilter.SelectedIndex = 0; SearchBox.Text = id;
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is not null)
        {
            ItemList.SelectedItem = item; ItemList.ScrollIntoView(item); SetItemDetail(item);
        }
        else if (OptimizationCatalog.Items.FirstOrDefault(i => i.Id == id) is { } definition)
            SetPlain(definition.DisplayName + "\n\n" + definition.DisplayDescription + "\n\n" +
                definition.DisplaySideEffect + "\n\n" + Str.T("Str.CommandDetectionFirst"));
        SearchBox.Focus();
    }
    private void SetOperationBusy(bool busy)
    {
        _operationBusy = busy;
        PresetControlsCard.IsEnabled = OptimizationItemsCard.IsEnabled = ConsentCheck.IsEnabled = !busy;
        ApplyButton.IsEnabled = RestoreButton.IsEnabled = OneClickButton.IsEnabled = DriverSettingsButton.IsEnabled = !busy;
        if (!busy) ApplyButton.IsEnabled = Items.Any(i => i.IsChecked);
    }
    internal OptimizationApplyPlan CaptureReviewPlan()
        => OptimizationApplyPlan.Capture(Items, item => ItemsView.Contains(item), AppState.GamePath);

    private async Task CompleteOperationAsync(Action renderResult)
    {
        string? refreshError = null;
        try
        {
            var refreshed = await (RefreshDetectionOverride?.Invoke()
                ?? (Window.GetWindow(this) as MainWindow ?? Application.Current.MainWindow as MainWindow)?.RefreshDetectionAsync()
                ?? Task.FromResult(false));
            if (refreshed) ReloadItemsFromState();
            else refreshError = Str.T("Str.DetectIncompleteRetry");
        }
        catch (Exception ex) { refreshError = OperationDiagnostics.Scrub(ex.Message); }

        ConsentCheck.IsChecked = false;
        // Rebuilding rows may change the detail panel. Keep the operation receipt last.
        renderResult();
        if (refreshError is not null)
        {
            var notice = NewPara(4);
            notice.Inlines.Add(R(Str.T("Str.OptRefreshFailed", refreshError), "WarningBrush"));
            OutputDoc.Blocks.Add(notice);
        }
    }

    private async Task ApplyReviewedAsync()
    {
        if (_operationBusy) return;
        if (ConsentCheck.IsChecked != true)
        {
            DialogService.Warning(Str.T("Str.ReviewTitle"), Str.T("Str.ConsentApply")); return;
        }
        var plan = CaptureReviewPlan();
        if (plan.Items.Count == 0)
        {
            DialogService.Info(Str.T("Str.ReviewTitle"), Str.T("Str.SelectAtLeastOneItem")); return;
        }
        SetOperationBusy(true);
        try
        {
            // 管理员预检放在审阅窗之前：审阅完点「应用」才被拦下，用户白走一轮。
            var adminItems = plan.Items.Where(i => i.RequiresAdmin).Select(i => i.Name).ToArray();
            if (adminItems.Length > 0 && !AdminHelper.IsAdministrator())
            {
                var elevate = DialogService.Confirm(Str.T("Str.NeedsAdmin"),
                    Str.T("Str.ReviewElevationItems", adminItems.Length,
                        string.Join(Str.T("Str.ListSeparator"), adminItems)),
                    confirmText: Str.T("Str.RestartAsAdminShort"), danger: false);
                if (elevate) AdminHelper.RestartAsAdministrator();
                return;
            }
            var review = new OptimizationReviewWindow(plan) { Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow };
            DialogService.PrepareWindowForTest?.Invoke(review);
            if (review.ShowDialog() != true) return;
            SetPlain(Str.T("Str.Applying"), "AccentBrush");
            var result = await (ApplyItemsOverride?.Invoke(plan.Ids, plan.GamePath)
                ?? NativeOptimizationEngine.ApplyItemsWithReceiptAsync(plan.Ids, plan.GamePath));
            await CompleteOperationAsync(() => SetApplyResult(result));
        }
        catch (Exception ex) { SetRawMonospace(ex.ToString()); }
        finally { SetOperationBusy(false); }
    }
}
