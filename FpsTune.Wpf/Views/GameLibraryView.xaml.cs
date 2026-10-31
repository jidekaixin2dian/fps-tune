using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class GameLibraryView : UserControl
{
    private sealed record GameRow(StateStore.GameProfile Profile, bool Accessible, string ProcessLabel, bool Current)
    {
        public string Name => new GameSwitcherItem(Profile.Name, Profile.Id, Profile.ExePath).DisplayLabel;
        public string CurrentLabel => Current ? Str.T("Str.LibraryCurrent") : "";
    }
    private IReadOnlyList<GameRow> _games = [];
    private RemovedGame? _removed;
    private bool _removedWasCurrent;
    private int _generation;
    private bool _loading;
    public GameLibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) => { GameContextService.GameSwitched += ContextChanged; RefreshLibrary(); };
        Unloaded += (_, _) => { GameContextService.GameSwitched -= ContextChanged; _generation++; _loading = false; };
    }
    private void ContextChanged() { if (!_loading) RefreshLibrary(); }
    private async void RefreshLibrary(string? selectedId = null, string? resultMessage = null)
    {
        var generation = ++_generation; _loading = true;
        var selected = selectedId ?? (GameList.SelectedItem as GameRow)?.Profile.Id;
        var currentPath = AppState.GamePath;
        DiscoverButton.IsEnabled = AddGameButton.IsEnabled = RefreshButton.IsEnabled = false;
        DetailPanel.IsEnabled = false; StatusText.Text = Str.T("Str.LibraryLoading");
        try
        {
            var rows = await Task.Run(() => GameLibraryService.Read().Select(p =>
            {
                var accessible = File.Exists(p.ExePath);
                var observation = accessible ? AutoRecordingService.ObserveProcess(p.ExePath) : new GameProcessObservation(null, false);
                var label = Str.T(!accessible ? "Str.LibraryInaccessible" : !observation.Reliable ? "Str.LibraryProcessUnknown"
                    : observation.Run is null ? "Str.LibraryNotRunning" : "Str.LibraryRunning");
                return new GameRow(p, accessible, label, p.ExePath.Equals(currentPath, StringComparison.OrdinalIgnoreCase));
            }).OrderBy(r => r.Name).ToArray());
            if (generation != _generation || !IsLoaded) return;
            _games = rows; ApplySearch(selected ?? rows.FirstOrDefault(r => r.Current)?.Profile.Id ?? rows.FirstOrDefault()?.Profile.Id);
            StatusText.Text = resultMessage ?? Str.T("Str.LibraryCount", rows.Length);
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                _games = []; GameList.ItemsSource = null;
                EmptyText.Text = StatusText.Text = PrivacyScrub.Sanitize(ex.Message); EmptyText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _loading = false; DiscoverButton.IsEnabled = AddGameButton.IsEnabled = RefreshButton.IsEnabled = true;
                ShowGame();
            }
        }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    { if (GameList is not null) ApplySearch((GameList.SelectedItem as GameRow)?.Profile.Id); }
    private void ApplySearch(string? selected)
    {
        var q = SearchBox.Text.Trim();
        var rows = _games.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.Profile.ExePath.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
        GameList.ItemsSource = rows;
        GameList.SelectedItem = rows.FirstOrDefault(r => r.Profile.Id == selected);
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Str.T(_games.Count == 0 ? "Str.LibraryEmpty" : "Str.LibraryNoMatch");
    }
    private void Game_Changed(object sender, SelectionChangedEventArgs e) => ShowGame();
    private void ShowGame()
    {
        var row = GameList.SelectedItem as GameRow;
        DetailPanel.IsEnabled = row is not null && !_loading;
        if (row is null) { GameNameText.Text = GamePathText.Text = ProcessText.Text = PlanText.Text = RecordingText.Text = NameBox.Text = ""; return; }
        GameNameText.Text = row.Name; GamePathText.Text = row.Profile.ExePath; ProcessText.Text = row.ProcessLabel; NameBox.Text = row.Name;
        SelectGameButton.IsEnabled = DetectGameButton.IsEnabled = PlanButton.IsEnabled = RecordButton.IsEnabled = DisplayButton.IsEnabled = row.Accessible;
        try { PlanText.Text = PreparationPlanStore.Describe(PreparationPlanStore.Resolve(row.Profile.ExePath)); }
        catch (Exception ex) { PlanText.Text = PrivacyScrub.Sanitize(ex.Message); }
        try
        {
            var template = SessionTemplateStore.Load(row.Profile.ExePath);
            RecordingText.Text = template is null ? Str.T("Str.SessionTemplateMissing") : Str.T("Str.LibraryRecording", SessionConditionRules.Describe(template));
        }
        catch (Exception ex) { RecordingText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private bool CanEdit()
    {
        if (!_loading && !GameContextService.IsBusy && !ExperimentRunner.IsRunning && !App.SessionService.IsRunning) return true;
        StatusText.Text = Str.T("Str.LibraryBusy"); return false;
    }
    private bool SelectTarget()
    {
        if (GameList.SelectedItem is not GameRow row || _loading) return false;
        if (!row.Profile.ExePath.Equals(AppState.GamePath, StringComparison.OrdinalIgnoreCase) && !CanEdit()) return false;
        if (ExperimentRunner.IsRunning || GamePreparationService.IsRunning) { StatusText.Text = Str.T("Str.LibraryBusy"); return false; }
        if (!File.Exists(row.Profile.ExePath)) { StatusText.Text = Str.T("Str.LibraryInaccessible"); return false; }
        if (!GameContextService.SwitchTo(row.Profile.ExePath)) { StatusText.Text = Str.T("Str.LibraryBusy"); return false; }
        return true;
    }
    private void Select_Click(object sender, RoutedEventArgs e) { if (SelectTarget()) RefreshLibrary(); }
    private void Route_Click(object sender, RoutedEventArgs e)
    { if (SelectTarget() && sender is Button { Tag: string route } && Window.GetWindow(this) is MainWindow main) main.NavigateTo(route); }
    private void AutoRecord_Click(object sender, RoutedEventArgs e)
    { if (!_loading && GameList.SelectedItem is GameRow row && Window.GetWindow(this) is MainWindow main) main.OpenRecordingOptions(row.Profile.ExePath); }
    private void PreparationHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || GameList.SelectedItem is not GameRow row) return;
        var dialog = new PreparationHistoryWindow(row.Profile.ExePath) { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog); dialog.ShowDialog();
    }
    private async void Plan_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || !SelectTarget()) return;
        if (Window.GetWindow(this) is MainWindow main) await main.OpenPreparationSettingsAsync();
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || GameList.SelectedItem is not GameRow row) return;
        try { GameLibraryService.Rename(row.Profile, NameBox.Text); GameContextService.NotifyLibraryChanged(); RefreshLibrary(row.Profile.Id); }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || GameList.SelectedItem is not GameRow row) return;
        var path = row.Profile.ExePath; var settings = SettingsService.Current;
        if (settings.AutoRecordGames.Contains(path, StringComparer.OrdinalIgnoreCase)
            || settings.AutoProfileBindings.Any(b => b.Enabled && b.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        { StatusText.Text = Str.T("Str.LibraryLinkedAutomation"); return; }
        if (!DialogService.Confirm(Str.T("Str.LibraryRemove"), Str.T("Str.LibraryRemoveConfirm", row.Name), danger: true)) return;
        if (!CanEdit()) return;
        try
        {
            _removed = GameLibraryService.Remove(row.Profile); _removedWasCurrent = row.Current; UndoRemoveButton.IsEnabled = true;
            GameContextService.NotifyLibraryChanged(); RefreshLibrary();
        }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void UndoRemove_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || _removed is not { } removed) return;
        try
        {
            GameLibraryService.UndoRemove(removed); _removed = null; UndoRemoveButton.IsEnabled = false;
            if (_removedWasCurrent && AppState.GamePath is null && File.Exists(removed.Profile.ExePath)) GameContextService.SwitchTo(removed.Profile.ExePath);
            GameContextService.NotifyLibraryChanged(); RefreshLibrary(removed.Profile.Id);
        }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit()) return;
        try { var game = GameContextService.PickAndAddGame(Window.GetWindow(this)); if (game is not null) { GameContextService.NotifyLibraryChanged(); RefreshLibrary(game.Id); } }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit()) return;
        var generation = ++_generation; _loading = true; DiscoverButton.IsEnabled = AddGameButton.IsEnabled = RefreshButton.IsEnabled = false;
        string? message = null; var added = 0;
        try
        {
            var candidates = await Task.Run(() => GamePathService.DetectAll(refresh: true).ToArray());
            if (generation != _generation || !IsLoaded) return;
            foreach (var game in candidates.Where(c => File.Exists(c.ExePath)))
            {
                var known = StateStore.FindGameByPath(game.ExePath) is not null;
                StateStore.AddGame(game.ExePath); if (!known) added++;
            }
            message = Str.T("Str.LibraryDiscovered", added);
            GameContextService.NotifyLibraryChanged();
        }
        catch (Exception ex) { message = Str.T("Str.LibraryDiscoverFailed", added, PrivacyScrub.Sanitize(ex.Message)); }
        finally
        {
            if (generation == _generation)
            { _loading = false; DiscoverButton.IsEnabled = AddGameButton.IsEnabled = RefreshButton.IsEnabled = true; RefreshLibrary(resultMessage: message); }
        }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLibrary();
}
