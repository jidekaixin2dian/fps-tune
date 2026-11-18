using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class StutterHistoryWindow : Window
{
    private sealed record HistoryRow(StutterEvidenceSnapshot Snapshot, string Game, string Time);

    private readonly CancellationTokenSource _lifetime = new();

    public StutterHistoryWindow()
    {
        InitializeComponent();
        HistoryHint.Text = Str.T("Str.StutterHistoryHint", StutterEvidence.HistoryLimit);
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    internal static void ShowFor(Window owner)
    {
        var window = new StutterHistoryWindow { Owner = owner };
        DialogService.PrepareWindowForTest?.Invoke(window);
        window.ShowDialog();
    }

    private async Task RefreshAsync()
    {
        if (!RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        var cancellation = _lifetime.Token;
        try
        {
            var history = await Task.Run(() => StutterEvidence.ReadHistory(cancellation), cancellation);
            if (!IsVisible) return;
            var selected = (HistoryList.SelectedItem as HistoryRow)?.Snapshot.At;
            var rows = history.Records.Select(snapshot => new HistoryRow(snapshot,
                string.IsNullOrWhiteSpace(snapshot.GameName) ? Str.T("Str.StutterUnknownGame") : snapshot.GameName,
                snapshot.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).ToArray();
            HistoryList.ItemsSource = rows;
            HistoryList.SelectedItem = rows.FirstOrDefault(row => row.Snapshot.At == selected) ?? rows.FirstOrDefault();
            EmptyText.Text = Str.T(history.Unavailable ? "Str.StutterHistoryUnavailable" : "Str.StutterHistoryEmpty");
            EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = history.Skipped > 0 ? Str.T("Str.StutterHistorySkipped", history.Skipped) : "";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (!cancellation.IsCancellationRequested) RefreshButton.IsEnabled = true; }
    }

    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
        {
            Details.Visibility = Visibility.Collapsed;
            return;
        }

        var record = row.Snapshot;
        var frames = record.StutterFramesMs.Where(value => double.IsFinite(value) && value > 0).ToArray();
        GameText.Text = row.Game;
        TimeText.Text = row.Time;
        PeakText.Text = Format(frames.Length > 0 ? frames.Max() : null, "ms", "0.0");
        MedianText.Text = Format(record.MedianMs > 0 ? record.MedianMs : null, "ms", "0.0");
        FpsText.Text = Format(record.Fps > 0 ? record.Fps : null, "FPS", "0.0");
        FrameText.Text = frames.Length > 0
            ? string.Join(" · ", frames.Select(value => Format(value, "ms", "0.0")))
            : Str.T(record.MedianMs > 0 ? "Str.StutterNoSpikes" : "Str.StutterNoFrames");
        GpuTitle.Text = string.IsNullOrWhiteSpace(record.GpuName) ? "GPU" : record.GpuName;
        GpuText.Text = Str.T("Str.StutterGpuDetail", Format(record.GpuUtilizationPercent, "%"),
            Format(record.GpuMemoryPercent, "%"), Format(record.GpuTemperatureC, "°C"), Format(record.GpuPowerW, "W"));
        SwitchText.Text = Str.T("Str.StutterSwitches", State(record.HagsState), State(record.MpoState));
        var background = record.BackgroundTop.Where(item => !string.IsNullOrWhiteSpace(item)).Take(3).ToArray();
        BackgroundText.Text = background.Length == 0 ? Str.T("Str.StutterNoBackground") : string.Join("\n", background);
        Details.Visibility = Visibility.Visible;
    }

    private static string Format(double? value, string unit, string format = "0")
        => value is { } number && double.IsFinite(number) ? number.ToString(format) + " " + unit : "—";

    private static string State(string? state) => Str.T(state switch
    {
        "on" => "Str.RecipeOn",
        "off" => "Str.Off",
        _ => "Str.StutterStateDefault"
    });

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
