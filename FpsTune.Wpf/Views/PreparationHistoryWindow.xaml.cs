using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class PreparationHistoryWindow : Window
{
    private sealed record Row(PreparationRun Run)
    {
        public string GameName => Run.GameName;
        public string Time => Run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string Outcome => Run.Outcome;
    }
    private readonly string? _gameKey;
    private IReadOnlyList<PreparationRun> _runs = [];
    public PreparationHistoryWindow(string? gamePath = null)
    {
        _gameKey = SessionConditionRules.CaptureGame(gamePath, null).GameKey;
        InitializeComponent();
        ((ComboBoxItem)ScopeBox.Items[0]).IsEnabled = _gameKey is not null;
        ScopeBox.SelectedIndex = _gameKey is null ? 1 : 0;
        try { _runs = PreparationHistory.Read(); ApplyScope(); }
        catch (Exception ex) { EmptyText.Text = StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void Scope_Changed(object sender, SelectionChangedEventArgs e) { if (RunList is not null) ApplyScope(); }
    private void ApplyScope()
    {
        var rows = _runs.Where(r => ScopeBox.SelectedIndex != 0 || r.GameKey == _gameKey).Select(r => new Row(r)).ToArray();
        RunList.ItemsSource = rows; RunList.SelectedIndex = rows.Length == 0 ? -1 : 0;
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = Str.T("Str.PreparationHistoryCount", rows.Length);
    }
    private void Run_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = RunList.SelectedItem as Row;
        ExportButton.IsEnabled = row is not null; Details.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        var run = row.Run;
        GameText.Text = run.GameName; TimeText.Text = Str.T("Str.PreparationHistoryTime", row.Time, run.Seconds);
        OutcomeText.Text = run.Outcome; OutcomeText.Foreground = (Brush)FindResource(run.Succeeded ? "OkBrush" : "WarningBrush");
        PlanText.Text = run.Plan; ErrorText.Text = run.Error;
        ErrorText.Visibility = string.IsNullOrEmpty(run.Error) ? Visibility.Collapsed : Visibility.Visible;
        StepList.ItemsSource = run.Steps.Select(s => new { s.Succeeded, Title = s.Name + " · " + Str.T(s.Succeeded ? "Str.PrepareStepDone" : "Str.PrepareStepAttention"),
            Detail = s.Detail + (string.IsNullOrWhiteSpace(s.Error) || s.Detail.Contains(s.Error, StringComparison.Ordinal) ? "" : "\n" + s.Error) }).ToArray();
    }
    internal bool ExportSelected(string path)
    {
        if (RunList.SelectedItem is not Row row) return false;
        try { PreparationHistory.Export(row.Run, path); StatusText.Text = Str.T("Str.PreparationHistoryExported"); return true; }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); return false; }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is not Row row) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = Str.T("Str.PreparationHistoryExport"), Filter = "JSON|*.json",
            FileName = "FpsTune-Preparation-" + row.Run.StartedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog(this) == true) ExportSelected(dialog.FileName);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
