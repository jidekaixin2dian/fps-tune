using System.Globalization;
using System.IO;
using System.Windows;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class DisplayQualityView
{
    private bool _frameDirty, _frameSyncing;
    private string? _frameGame, _frameStatus;

    private void ApplyFrameLimitState(DisplayQualityService.DrsGameSettings state)
    {
        if (_frameGame != state.GameExe) { _frameDirty = false; _frameStatus = null; _frameGame = state.GameExe; }
        _frameSyncing = true;
        if (!_frameDirty)
        {
            MainCapBox.Text = (state.FrameRateLimit ?? 144).ToString(CultureInfo.InvariantCulture);
            BackgroundCapBox.Text = (state.BackgroundFrameRateLimit ?? 30).ToString(CultureInfo.InvariantCulture);
            MainCapCheck.IsChecked = BackgroundCapCheck.IsChecked = false;
        }
        _frameSyncing = false;
        FrameControls.IsEnabled = true;
        FrameApplyButton.IsEnabled = MainCapCheck.IsChecked == true || BackgroundCapCheck.IsChecked == true;
        FrameRestoreButton.IsEnabled = state.FrameLimitsRestorable;
        FrameCurrentText.Text = Str.T("Str.FrameCurrent", CapLabel(state.FrameRateLimit), CapLabel(state.BackgroundFrameRateLimit));
        FrameStateText.Text = _frameStatus ?? Str.T("Str.FrameRange");
        UpdateFrameBudget();
    }
    private static string CapLabel(uint? value) => value switch
    {
        null => Str.T("Str.FollowDriverGame"), 0 => Str.T("Str.FrameOff"), _ => value + " FPS"
    };
    private void UpdateFrameBudget() => FrameBudgetText.Text = uint.TryParse(MainCapBox.Text, out var fps) && fps > 0
        && DisplayQualityService.IsValidFrameLimit(fps) ? Str.T("Str.FrameBudget", fps, 1000.0 / fps) : Str.T("Str.FrameForegroundRange");

    private void FrameInputs_Changed(object sender, RoutedEventArgs e)
    {
        if (_frameSyncing || FrameApplyButton is null || FrameBudgetText is null) return;
        _frameDirty = true;
        FrameApplyButton.IsEnabled = !_busy && FrameControls.IsEnabled && (MainCapCheck.IsChecked == true || BackgroundCapCheck.IsChecked == true);
        UpdateFrameBudget();
    }
    private async void FrameApply_Click(object sender, RoutedEventArgs e) => await ChangeFrameLimitsAsync(restore: false);
    private async void FrameRestore_Click(object sender, RoutedEventArgs e) => await ChangeFrameLimitsAsync(restore: true);

    private async Task ChangeFrameLimitsAsync(bool restore)
    {
        if (_busy || string.IsNullOrWhiteSpace(AppState.GamePath) || !File.Exists(AppState.GamePath)) return;
        var exe = Path.GetFileName(AppState.GamePath);
        uint? main = null, background = null;
        if (!restore)
        {
            if (MainCapCheck.IsChecked == true)
            {
                if (!uint.TryParse(MainCapBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    || !DisplayQualityService.IsValidFrameLimit(value)) { FrameStateText.Text = Str.T("Str.FrameRange"); return; }
                main = value;
            }
            if (BackgroundCapCheck.IsChecked == true)
            {
                if (!uint.TryParse(BackgroundCapBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    || !DisplayQualityService.IsValidFrameLimit(value, background: true)) { FrameStateText.Text = Str.T("Str.FrameRange"); return; }
                background = value;
            }
            if (main is null && background is null) { FrameStateText.Text = Str.T("Str.FrameSelectOne"); return; }
        }
        var limits = new DisplayQualityService.FrameLimits(main, background);
        var details = restore ? Str.T("Str.FrameRestoreConfirm", exe) : Str.T("Str.FrameConfirm", exe,
            main.HasValue ? CapLabel(main) : Str.T("Str.FrameKeep"), background.HasValue ? CapLabel(background) : Str.T("Str.FrameKeep"));
        if (!DialogService.Confirm(Str.T("Str.FrameLimitTitle"), details, confirmText: Str.T(restore ? "Str.RestoreOriginal" : "Str.Apply"))) return;
        _busy = true; ++_refreshGen;
        SetDrsInputsEnabled(false); DrsApplyButton.IsEnabled = DrsRestoreButton.IsEnabled = false;
        FrameStateText.Text = Str.T(restore ? "Str.Restoring2" : "Str.Writing");
        try
        {
            var changed = await Task.Run(() =>
            {
                if (restore) return DisplayQualityService.RestoreFrameLimits(exe);
                DisplayQualityService.ApplyFrameLimits(exe, limits); return true;
            });
            _frameDirty = false;
            _frameStatus = changed ? Str.T(restore ? "Str.FrameRestored" : "Str.FrameApplied") : Str.T("Str.NothingToRestore");
        }
        catch (NvdrsException ex) when (ex.Status == -175) { _frameStatus = HandleDrsAccessDenied(ex, restoring: restore); }
        catch (Exception ex) { _frameStatus = Str.T("Str.FrameFailed", OperationDiagnostics.Scrub(ex.Message)); }
        finally { _busy = false; Refresh(); }
    }
}
