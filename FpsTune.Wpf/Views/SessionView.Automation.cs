using System.IO;
using System.Windows;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class SessionView
{
    private sealed class AutoGameRow(string path, bool selected)
    {
        public string Path { get; } = path;
        public string Name => GamePathService.LabelFor(Path);
        public bool Selected { get; set; } = selected;
    }
    private void InitializeAutoRecording()
    {
        AutoRecordEnabled.IsChecked = SettingsService.Current.AutoRecordEnabled;
        RefreshAutoGames(SettingsService.Current.AutoRecordGames);
        UpdateAutoRecordStatus();
    }
    private void RefreshAutoGames(IEnumerable<string> selections)
    {
        var selected = selections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = StateStore.LoadGames().Select(g => g.ExePath).Concat(selected);
        if (AppState.GamePath is { } current) paths = paths.Append(current);
        AutoRecordGames.ItemsSource = paths.Where(p => Path.IsPathFullyQualified(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(GamePathService.LabelFor)
            .Select(p => new AutoGameRow(p, selected.Contains(p))).ToArray();
        AutoRecordEmpty.Visibility = AutoRecordGames.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AutoGamesRefresh_Click(object sender, RoutedEventArgs e)
        => RefreshAutoGames(AutoRecordGames.Items.Cast<AutoGameRow>().Where(g => g.Selected).Select(g => g.Path));
    private void AutoRecordSave_Click(object sender, RoutedEventArgs e)
    {
        var paths = AutoRecordGames.Items.Cast<AutoGameRow>().Where(g => g.Selected).Select(g => g.Path).ToList();
        var enabled = AutoRecordEnabled.IsChecked == true;
        if (enabled && paths.Count == 0) { AutoRecordStatus.Text = Str.T("Str.AutoRecordSelectGames"); return; }
        if (paths.Count > 32) { AutoRecordStatus.Text = Str.T("Str.AutoRecordTooMany"); return; }
        var settings = SettingsService.Current;
        var previousEnabled = settings.AutoRecordEnabled; var previousGames = settings.AutoRecordGames;
        settings.AutoRecordEnabled = enabled; settings.AutoRecordGames = paths;
        try { SettingsService.Save(settings); }
        catch (Exception ex)
        {
            settings.AutoRecordEnabled = previousEnabled; settings.AutoRecordGames = previousGames;
            AutoRecordStatus.Text = PrivacyScrub.Sanitize(ex.Message); return;
        }
        _automation?.ApplySettings();
        UpdateAutoRecordStatus();
    }
    private void UpdateAutoRecordStatus()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(UpdateAutoRecordStatus); return; }
        AutoRecordStatus.Text = _automation?.Status ?? Str.T(SettingsService.Current.AutoRecordEnabled ? "Str.AutoRecordWaiting" : "Str.AutoRecordOff");
    }
    private void HudShow_Click(object sender, RoutedEventArgs e) => _hud.Show();
    private void HudHide_Click(object sender, RoutedEventArgs e) => _hud.Close();
    private void HudUnlock_Click(object sender, RoutedEventArgs e) => _hud.Unlock();
}
