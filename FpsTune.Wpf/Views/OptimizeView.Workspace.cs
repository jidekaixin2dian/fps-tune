using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class OptimizeView
{
    private bool _operationBusy;
    private string _scopeFilter = "all";
    internal Func<IReadOnlyList<string>, string?, Task<RunResult>>? ApplyItemsOverride { get; set; }
    internal Func<Task<bool>>? RefreshDetectionOverride { get; set; }
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
    private void ShowSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        AllGroups.IsChecked = true; SearchBox.Clear(); ScopeFilter.SelectedIndex = 2;
    }
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
        else if (ItemCatalog.All.FirstOrDefault(i => i.Id == id) is { } definition)
            SetPlain(definition.DisplayName + "\n\n" + definition.DisplayDescription + "\n\n" +
                definition.DisplaySideEffect + "\n\n" + Str.T("Str.CommandDetectionFirst"));
        SearchBox.Focus();
    }
    private void SetOperationBusy(bool busy)
    {
        _operationBusy = busy;
        PresetControlsCard.IsEnabled = OptimizationItemsCard.IsEnabled = ConsentCheck.IsEnabled = !busy;
        ApplyButton.IsEnabled = RestoreButton.IsEnabled = OneClickButton.IsEnabled = !busy;
        if (!busy) ApplyButton.IsEnabled = Items.Any(i => i.IsChecked);
    }
    internal OptimizationApplyPlan CaptureReviewPlan()
        => OptimizationApplyPlan.Capture(Items, item => ItemsView.Contains(item), AppState.GamePath);

    private async Task CompleteOperationAsync(RunResult result)
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
        SetApplyResult(result);
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
            var review = new OptimizationReviewWindow(plan) { Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow };
            DialogService.PrepareWindowForTest?.Invoke(review);
            if (review.ShowDialog() != true) return;
            if (plan.Items.Any(i => i.RequiresAdmin) && !AdminHelper.IsAdministrator())
            {
                if (DialogService.Confirm(Str.T("Str.NeedsAdmin"), Str.T("Str.ReviewElevation"))) AdminHelper.RestartAsAdministrator();
                return;
            }
            SetPlain(Str.T("Str.Applying"), "AccentBrush");
            var result = await (ApplyItemsOverride?.Invoke(plan.Ids, plan.GamePath)
                ?? OptimizationEngine.ApplyItemsAsync(plan.Ids, plan.GamePath));
            await CompleteOperationAsync(result);
        }
        catch (Exception ex) { SetRawMonospace(ex.ToString()); }
        finally { SetOperationBusy(false); }
    }
}
