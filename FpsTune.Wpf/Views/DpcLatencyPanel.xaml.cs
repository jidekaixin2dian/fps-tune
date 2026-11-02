using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class DpcLatencyPanel : UserControl
{
    private bool _busy;

    public DpcLatencyPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAdminState();
    }

    private void UpdateAdminState()
    {
        var admin = AdminHelper.IsAdministrator();
        DpcAdminText.Text = Str.T(admin ? "Str.DpcAdminReady" : "Str.DpcAdminRequired");
        DpcRunButton.Content = Str.T(admin ? "Str.DpcStart" : "Str.DpcStartElevated");
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!int.TryParse((DpcDurationCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var seconds))
            seconds = DpcLatencyService.DefaultSeconds;
        _busy = true;
        DpcRunButton.IsEnabled = false;
        DpcProgress.Visibility = Visibility.Visible;
        DpcResultPanel.Visibility = Visibility.Collapsed;
        DpcStatusText.Text = Str.T(AdminHelper.IsAdministrator() ? "Str.DpcSampling" : "Str.DpcRequestingAdmin", seconds);
        try
        {
            var report = AdminHelper.IsAdministrator()
                ? await DpcLatencyService.MeasureAsync(seconds)
                : await DpcLatencyService.MeasureElevatedAsync(seconds);
            DisplayReport(report);
        }
        catch (Exception ex) { DpcStatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally
        {
            _busy = false;
            DpcRunButton.IsEnabled = true;
            DpcProgress.Visibility = Visibility.Collapsed;
            UpdateAdminState();
        }
    }

    internal void DisplayReport(DpcLatencyReport report)
    {
        if (!report.Ok || report.Error is { Length: > 0 })
        {
            DpcStatusText.Text = report.Error ?? Str.T("Str.DpcReadFailed");
            DpcResultPanel.Visibility = Visibility.Collapsed;
            return;
        }
        DpcResultPanel.Visibility = Visibility.Visible;
        DpcStatusText.Text = Str.T("Str.DpcComplete", report.DurationSeconds);
        DpcVerdictText.Text = report.Verdict;
        var accent = report.Severity switch
        {
            "high" => (Brush)FindResource("DangerBrush"),
            "watch" => (Brush)FindResource("WarningBrush"),
            _ => (Brush)FindResource("OkBrush"),
        };
        DpcVerdictText.Foreground = accent;
        DpcVerdictBadge.Background = new SolidColorBrush(Color.FromArgb(36, ((SolidColorBrush)accent).Color.R,
            ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B));
        DpcMeasuredText.Text = Str.T("Str.DpcMeasuredAt", report.MeasuredAt.ToLocalTime().ToString("HH:mm:ss"),
            report.ProcessorCount, report.DurationSeconds);
        DpcCpuText.Text = report.DpcCpuPercent.ToString("0.00") + "%";
        DpcMaxText.Text = report.DpcMaxMs.ToString("0.00") + " ms";
        IsrCpuText.Text = report.IsrCpuPercent.ToString("0.00") + "%";
        DpcSamplesText.Text = (report.DpcCount + report.IsrCount).ToString("N0");
        DpcDriversList.ItemsSource = report.Drivers;
    }
}
