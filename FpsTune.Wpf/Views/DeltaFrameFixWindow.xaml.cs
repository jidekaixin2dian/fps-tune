using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;

namespace FpsTune.Wpf.Views;

public partial class DeltaFrameFixWindow : Window
{
    public DeltaFrameFixWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { Refresh(); PlayEntrance(); };
    }

    /// <summary>弹窗入场：淡入 + 轻微上浮与放大，系统关闭动画或低配模式下直接静态呈现。</summary>
    private void PlayEntrance()
    {
        if (!UiMotion.Enabled) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        var grow = new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        ShellScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        ShellScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
        ShellOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { /* 鼠标已释放，忽略 */ }
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
