using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class SessionView
{
    private sealed record HistoryGameOption(string Key, string Label);
    private bool _updatingHistoryFilters;
    private IReadOnlyList<PerformanceSession> _visibleSessions = [];
    private IReadOnlyList<SessionListVm> _historyRows = [];

    private void RebuildHistoryGames()
    {
        var previous = (HistoryGameFilter.SelectedItem as HistoryGameOption)?.Key;
        var games = _sessions.GroupBy(s => s.Conditions?.GameKey ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new HistoryGameOption(g.Key, g.First().Conditions?.GameName ?? Str.T("Str.ConditionsUnknown")))
            .OrderBy(g => g.Label).ThenBy(g => g.Key).ToList();
        // Different paths can have identical names. Make both choices distinguishable without exposing paths.
        var duplicates = games.GroupBy(g => g.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        games = games.Select(g => duplicates.Contains(g.Label) && g.Key.Length >= 8
            ? g with { Label = $"{g.Label} · {g.Key[..8]}" } : g).ToList();
        games.Insert(0, new("*", Str.T("Str.SessionHistoryAllGames")));
        _updatingHistoryFilters = true;
        try
        {
            HistoryGameFilter.ItemsSource = games;
            HistoryGameFilter.SelectedItem = games.FirstOrDefault(g => g.Key == previous) ?? games[0];
        }
        finally { _updatingHistoryFilters = false; }
    }

    private void HistorySearch_Changed(object sender, TextChangedEventArgs e) => ApplyHistoryFilters();
    private void HistoryFilter_Changed(object sender, RoutedEventArgs e) => ApplyHistoryFilters();
    private void ResetHistoryFilters_Click(object sender, RoutedEventArgs e)
    {
        _updatingHistoryFilters = true;
        try { HistorySearchBox.Clear(); HistoryGameFilter.SelectedIndex = 0; HistoryFpsOnly.IsChecked = false; }
        finally { _updatingHistoryFilters = false; }
        ApplyHistoryFilters();
    }

    private void ApplyHistoryFilters()
    {
        if (!IsInitialized || _updatingHistoryFilters) return;
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().Select(s => s.Id).ToHashSet();
        var query = HistorySearchBox.Text.Trim();
        var game = (HistoryGameFilter.SelectedItem as HistoryGameOption)?.Key ?? "*";
        _visibleSessions = _sessions.Where(s => (game == "*" || string.Equals(s.Conditions?.GameKey ?? "", game, StringComparison.OrdinalIgnoreCase))
            && (HistoryFpsOnly.IsChecked != true || s.Fps is { Samples: > 0 })
            && (query.Length == 0 || new[] { s.Name, s.Conditions?.GameName, s.Conditions?.Quality, s.Conditions?.Scene,
                    SessionConditionRules.Resolution(s.Conditions) }.Any(v => v?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
            .ToArray();
        var ids = _visibleSessions.Select(s => s.Id).ToHashSet();
        var rows = _historyRows.Where(s => ids.Contains(s.Id)).ToArray();
        HistoryList.ItemsSource = rows;
        foreach (var row in rows.Where(s => selected.Contains(s.Id))) HistoryList.SelectedItems.Add(row);
        HistoryCountText.Text = Str.T("Str.SessionHistoryCount", rows.Length, _sessions.Count);
        HistoryEmptyText.Text = Str.T(_sessions.Count == 0 ? "Str.NoSessionsYet" : "Str.SessionHistoryNoMatch");
        HistoryEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (HistoryList.SelectedItems.Count == 0 && !Service.IsRunning)
        { InsightList.ItemsSource = null; InsightTargetText.Text = Str.T("Str.SelectSessionHint"); }
        RefreshCompareSources();
    }
}
