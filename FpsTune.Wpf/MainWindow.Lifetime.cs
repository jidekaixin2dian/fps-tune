using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

/// <summary>
/// 主窗口的生命周期宿主：关闭流程（用户关窗 vs 程序内关闭）、关窗去向、托盘常驻与恢复。
/// <para>
/// 边界：这里决定「窗口什么时候消失、消失后从哪里回来」，不改设置项的内容语义，
/// 也不碰页面导航与通知卡。托盘图标本身的创建/气泡/菜单在 <see cref="TrayService"/>。
/// </para>
/// </summary>
public partial class MainWindow
{
    private bool _closeChoiceOpen;
    private WindowState _restoreState = WindowState.Normal;
    internal Func<bool> EnsureTray { get; set; } = TrayService.EnsureCreated;

    /// <summary>开关开着、守护没被「停止」、并且用户勾了「关闭后保持」时，关窗不能顺手停掉滤镜。</summary>
    private static bool KeepsFilterWatcherRunning()
        => SettingsService.Current.FilterWatcherKeepAfterExit
           && SettingsService.Current.FilterWatcherEnabled
           && App.FilterWatcher is { Paused: false };

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
            // 「关闭后保持」：独立程序滤镜还在跑的时候，关窗不退出、留托盘继续跟随（否则游戏滤镜会跟着一起停）。
            if (action == CloseBehavior.Ask && KeepsFilterWatcherRunning()) action = CloseBehavior.Tray;
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


    }
    private void RestoreForExitFeedback()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) RestoreFromTray();
    }

    // ---------- 托盘常驻 ----------

    private async void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateRootClip();
        UpdateCaptionState();
        if (WindowState == WindowState.Minimized && SettingsService.Current.MinimizeToTray)
        {
            // 先让最小化动画落定再决定去向: 托盘图标挂载成功才隐藏窗口,
            // 否则保持任务栏最小化(避免窗口隐藏后托盘也没有、失去入口)。
            // ApplicationIdle 不等于 DWM 过渡完成，给系统最小化动画留出时间。
            await Task.Delay(250);
            if (!IsLoaded || WindowState != WindowState.Minimized)
                return;
            if (TrayService.EnsureCreated())
            {
                Hide();
                TrayService.ShowMinimizedHint();
            }
        }
    }

    /// <summary>从托盘/热键恢复主窗口。</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Activate();
        Focus();
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
