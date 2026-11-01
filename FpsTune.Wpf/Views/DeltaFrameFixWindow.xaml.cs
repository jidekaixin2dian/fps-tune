using System.Windows;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class DeltaFrameFixWindow : Window
{
    public DeltaFrameFixWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        try
        {
            var scan = DeltaFrameFixService.Scan();
            ScanText.Text = scan.Error is { } error ? error : Str.T("Str.DeltaFixScan",
                scan.GameDir ?? "—",
                FormatMiB(scan.PsoCacheBytes), FormatMiB(scan.SavedBytes), FormatMiB(scan.DxCacheBytes));
            PsoFixButton.IsEnabled = scan.GameFound;
            LegacyFixButton.IsEnabled = scan.GameFound;
        }
        catch (Exception ex) { ScanText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private static string FormatMiB(long bytes) => $"{bytes / (1024d * 1024):F1} MiB";

    private async void PsoFix_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(Str.T("Str.DeltaFixTitle"), Str.T("Str.DeltaFixPsoConfirm"), confirmText: Str.T("Str.DeltaFixPsoAction"))) return;
        PsoFixButton.IsEnabled = false;
        try
        {
            var step = await Task.Run(() => DeltaFrameFixService.FixPsoCache(null));
            ResultText.Text = Str.T("Str.DeltaFixStepResult", step.Name, Str.T(step.Succeeded ? "Str.PrepareStepDone" : "Str.PrepareStepAttention"), step.Detail);
            Refresh();
        }
        catch (Exception ex) { ResultText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { PsoFixButton.IsEnabled = true; }
    }

    private async void LegacyFix_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(Str.T("Str.DeltaFixTitle"), Str.T("Str.DeltaFixLegacyConfirm"), confirmText: Str.T("Str.DeltaFixLegacyAction"))) return;
        LegacyFixButton.IsEnabled = false;
        try
        {
            var steps = await Task.Run(() => DeltaFrameFixService.FixLegacy(null));
            ResultText.Text = string.Join("\n", steps.Select(s => Str.T("Str.DeltaFixStepResult",
                s.Name, Str.T(s.Succeeded ? "Str.PrepareStepDone" : "Str.PrepareStepAttention"), s.Detail)));
            Refresh();
        }
        catch (Exception ex) { ResultText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { LegacyFixButton.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
