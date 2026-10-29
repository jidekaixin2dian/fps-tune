using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class SessionView
{
    private SessionComparison? _comparison;
    private void CompareSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = HistoryList.SelectedItems.OfType<SessionListVm>().Select(s => s.Id).ToHashSet();
        var selected = _visibleSessions.Where(s => ids.Contains(s.Id)).OrderBy(s => s.StartedAt).ThenBy(s => s.Id).ToArray();
        if (selected.Length != 2) return;
        CompareA.SelectedItem = CompareA.Items.Cast<CompareSourceVm>().Single(s => s.Id == selected[0].Id);
        CompareB.SelectedItem = CompareB.Items.Cast<CompareSourceVm>().Single(s => s.Id == selected[1].Id);
        FocusComparison();
    }
    internal void FocusComparison()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            UpdateLayout();
            var top = ComparisonCard.TranslatePoint(new Point(0, 0), RootScroll).Y;
            RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset + top - 12);
        });
    }
    private void SwapComparison_Click(object sender, RoutedEventArgs e)
    {
        var a = CompareA.SelectedItem; var b = CompareB.SelectedItem;
        CompareA.SelectedItem = b; CompareB.SelectedItem = a;
    }
    internal void ExportComparisonTo(string path) => SessionComparisonReport.Export(_comparison
        ?? throw new InvalidOperationException(Str.T("Str.SelectTwoSessions")), path);
    private void ExportComparison_Click(object sender, RoutedEventArgs e)
    {
        var comparison = _comparison;
        if (comparison is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = Str.T("Str.ComparisonExport"), Filter = "HTML|*.html",
            DefaultExt = ".html", FileName = $"FpsTune-Comparison-{DateTime.Now:yyyyMMdd-HHmmss}.html" };
        if (dialog.ShowDialog() != true) return;
        try { SessionComparisonReport.Export(comparison, dialog.FileName); ComparisonExportStatus.Text = Str.T("Str.ComparisonExported"); }
        catch (Exception ex) { ComparisonExportStatus.Text = Str.T("Str.ComparisonExportFailed", PrivacyScrub.Sanitize(ex.Message)); }
    }
}
