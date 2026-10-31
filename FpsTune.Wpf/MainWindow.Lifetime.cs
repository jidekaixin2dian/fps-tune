using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

public partial class MainWindow
{
    private bool _closeChoiceOpen;
    private WindowState _restoreState = WindowState.Normal;
    internal Func<bool> EnsureTray { get; set; } = TrayService.EnsureCreated;

    // User actions share one entry. Programmatic Close remains an intentional exit,
    // including tray Exit, updating, session-save continuation and OS shutdown.
    internal void RequestUserClose()
    {
        if (_closeChoiceOpen || _savingSessionOnExit || !IsLoaded) return;
        _closeChoiceOpen = true;
        try
        {
            var action = CloseBehavior.Normalize(SettingsService.Current.CloseAction);
            var remember = false;
            if (action == CloseBehavior.Ask)
            {
                var dialog = new CloseChoiceWindow { Owner = this };
                DialogService.PrepareWindowForTest?.Invoke(dialog);
                if (dialog.ShowDialog() != true || dialog.Choice is not { } choice) return;
                action = choice.Action; remember = choice.Remember;
            }
            // Do not disappear if the shell cannot provide a recovery entry.
            if (action == CloseBehavior.Tray && !EnsureTray())
            {
                DialogService.Warning(Str.T("Str.CloseChoiceTitle"), Str.T("Str.CloseTrayUnavailable")); return;
            }
            if (remember)
            {
                var settings = SettingsService.Current; var previous = settings.CloseAction;
                try { settings.CloseAction = action; SettingsService.Save(settings); }
                catch (Exception ex)
                {
                    settings.CloseAction = previous;
                    DialogService.Warning(Str.T("Str.CloseChoiceTitle"), Str.T("Str.CloseRememberFailed") + "\n" + ex.Message); return;
                }
            }
            if (action == CloseBehavior.Exit) Close();
            else { Hide(); if (UserDataPaths.RootOverride is null) TrayService.ShowMinimizedHint(force: true); }
        }
        finally { _closeChoiceOpen = false; }
    }

    private async void OnMainClosing(object? sender, CancelEventArgs e)
    {
        if (_savingSessionOnExit) { e.Cancel = true; return; }
        if (GamePreparationService.IsRunning)
        {
            e.Cancel = true; RestoreForExitFeedback();
            DialogService.Info(Str.T("Str.PrepareTitle"), Str.T("Str.PrepareBusy")); return;
        }
        if (App.SessionService.IsRunning)
        {
            e.Cancel = true;
            _savingSessionOnExit = true;
            try
            {
                await App.SessionService.StopAsync();
                // A fresh callback closes only after persistence has completed.
                _ = Dispatcher.BeginInvoke(Close);
            }
            catch (Exception ex)
            {
                RestoreForExitFeedback();
                DialogService.Warning(Str.T("Str.SessionSaveFailed"), Str.T("Str.SessionSaveFailedHint") + "\n\n" + ex.Message);
            }
            finally { _savingSessionOnExit = false; }
            return;
        }
        if (_pageCache.TryGetValue("ab", out var page) && page is AbExperimentView ab) ab.CancelPendingRun();
    }
    private void RestoreForExitFeedback()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) RestoreFromTray();
    }

    internal void UpdateCaptionState()
    {
        var maximized = WindowState == WindowState.Maximized;
        CaptionMaxGlyph.Data = Geometry.Parse(maximized ? "M2 0 H10 V8 M0 2 H8 V10 H0 Z" : "M0 0 H10 V10 H0 Z");
        var key = maximized ? "Str.RestoreWindow" : "Str.Maximize";
        CaptionMax.SetResourceReference(ToolTipProperty, key);
        CaptionMax.SetResourceReference(AutomationProperties.NameProperty, key);
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
    }
}
