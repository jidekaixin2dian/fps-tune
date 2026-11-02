using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>三角洲掉帧修复：独立页面的三步向导（扫描 → PSO → Saved），每步可单独执行、单独还原。</summary>
public partial class DeltaFixView : UserControl
{
    private bool _busy;

    public DeltaFixView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>回到本页时重新扫描一次（PSO/Saved 缓存可能已被游戏重建）。</summary>
    internal void Reload() => Refresh();

    private void Refresh()
    {
        try
        {
            var scan = DeltaFrameFixService.Scan();
            ScanText.Text = scan.Error is { } error ? error : Str.T("Str.DeltaFixScan",
                scan.GameDir ?? "—",
                FormatMiB(scan.PsoCacheBytes), FormatMiB(scan.SavedBytes), FormatMiB(scan.DxCacheBytes));
            UpdateWizard(scan);
        }
        catch (Exception ex) { ScanText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    /// <summary>步骤条与两个按钮的可用性全部由扫描结果决定，执行/还原完立刻按新状态重画。</summary>
    private void UpdateWizard(DeltaFrameScan scan)
    {
        SetStep(StepPill1, StepState1, scan.GameFound);
        SetStep(StepPill2, StepState2, scan.PsoBackup is not null);
        SetStep(StepPill3, StepState3, scan.SavedBackup is not null || scan.DxBackup is not null);
        PsoFixButton.IsEnabled = scan.GameFound && !_busy;
        LegacyFixButton.IsEnabled = scan.GameFound && !_busy;
        PsoRestoreButton.IsEnabled = scan.PsoBackup is not null && !_busy;
        LegacyRestoreButton.IsEnabled = (scan.SavedBackup is not null || scan.DxBackup is not null) && !_busy;
        PsoStatusText.Text = scan.PsoBackup is null ? "" : Str.T("Str.DeltaFixBackupAt", scan.PsoBackup);
        LegacyStatusText.Text = string.Join("\n", new[] { scan.SavedBackup, scan.DxBackup }
            .Where(path => path is not null)
            .Select(path => Str.T("Str.DeltaFixBackupAt", path!)));
    }

    /// <summary>已完成 = 该步的备份目录已在（说明这步做过），边框转成强调色。</summary>
    private static void SetStep(Border pill, TextBlock state, bool done)
    {
        pill.SetResourceReference(Control.BorderBrushProperty, done ? "AccentBrush" : "BorderBrush");
        state.Text = Str.T(done ? "Str.DeltaFixStateDone" : "Str.DeltaFixStatePending");
        state.SetResourceReference(TextBlock.ForegroundProperty, done ? "AccentBrush" : "TextMutedBrush");
    }

    private static string FormatMiB(long bytes) => $"{bytes / (1024d * 1024):F1} MiB";

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        try { await Task.Run(() => DeltaFrameFixService.Scan()); Refresh(); }
        finally { RescanButton.IsEnabled = true; }
    }

    private async void PsoFix_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(Str.T("Str.DeltaFixTitle"), Str.T("Str.DeltaFixPsoConfirm"), confirmText: Str.T("Str.DeltaFixPsoAction"))) return;
        _busy = true; PsoFixButton.IsEnabled = false;
        try
        {
            var step = await Task.Run(() => DeltaFrameFixService.FixPsoCache(null));
            ResultText.Text = Str.T("Str.DeltaFixStepResult", step.Name, Str.T(step.Succeeded ? "Str.PrepareStepDone" : "Str.PrepareStepAttention"), step.Detail);
            Refresh();
        }
        catch (Exception ex) { ResultText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _busy = false; PsoFixButton.IsEnabled = true; }
    }

    private async void LegacyFix_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(Str.T("Str.DeltaFixTitle"), Str.T("Str.DeltaFixLegacyConfirm"), confirmText: Str.T("Str.DeltaFixLegacyAction"))) return;
        _busy = true; LegacyFixButton.IsEnabled = false;
        try
        {
            var steps = await Task.Run(() => DeltaFrameFixService.FixLegacy(null));
            ResultText.Text = Format(steps);
            Refresh();
        }
        catch (Exception ex) { ResultText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _busy = false; LegacyFixButton.IsEnabled = true; }
    }

    private async void PsoRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(Str.T("Str.DeltaFixTitle"), Str.T("Str.DeltaFixRestoreConfirm"), confirmText: Str.T("Str.DeltaFixRestoreStep"))) return;
        _busy = true; PsoRestoreButton.IsEnabled = false;
        try
        {
            var step = await Task.Run(() => DeltaFrameFixService.RestorePsoCache(null));
            ResultText.Text = Format([step]);
        }
        catch (Exception ex) { ResultText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _busy = false; Refresh(); }
    }

    private async void LegacyRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(Str.T("Str.DeltaFixTitle"), Str.T("Str.DeltaFixRestoreConfirm"), confirmText: Str.T("Str.DeltaFixRestoreStep"))) return;
        _busy = true; LegacyRestoreButton.IsEnabled = false;
        try
        {
            var steps = await Task.Run(() => new[] { DeltaFrameFixService.RestoreSaved(null), DeltaFrameFixService.RestoreDxCache() });
            ResultText.Text = Format(steps);
        }
        catch (Exception ex) { ResultText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _busy = false; Refresh(); }
    }

    private static string Format(IEnumerable<DeltaFixStep> steps)
        => string.Join("\n", steps.Select(s => Str.T("Str.DeltaFixStepResult",
            s.Name, Str.T(s.Succeeded ? "Str.PrepareStepDone" : "Str.PrepareStepAttention"), s.Detail)));
}
