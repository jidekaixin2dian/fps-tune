using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 系统托盘常驻：托盘图标、右键菜单、气泡通知。
/// 只在 UI 线程调用；图标借用 WinForms NotifyIcon，菜单是 WinForms ContextMenuStrip。
/// </summary>
public static class TrayService
{
    private const string HotkeyHint = "\n\n最小化后不占任务栏，可从托盘图标或全局热键 Ctrl+Alt+F 呼出。";

    private static System.Windows.Forms.NotifyIcon? _icon;
    private static bool _minimizeHintShown;

    /// <summary>托盘功能当前是否开启（跟随设置项）。</summary>
    public static bool IsEnabled => SettingsService.Current.MinimizeToTray;

    /// <summary>按当前设置创建或销毁托盘图标；设置页切换开关时调用。</summary>
    public static void ApplySettings()
    {
        if (IsEnabled)
            EnsureCreated();
        else
            Dispose();
    }

    public static void EnsureCreated()
    {
        var icon = _icon;
        if (icon is not null)
        {
            icon.Visible = true;
            return;
        }

        icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "FPS 帧律",
            Visible = true
        };
        try
        {
            var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (sri is not null)
                icon.Icon = new System.Drawing.Icon(sri.Stream);
        }
        catch
        {
            // 图标读取失败时托盘仍可用（显示默认空白图标），不影响功能。
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("打开优化页", null, (_, _) => ShowMainWindow("opt"));
        menu.Items.Add("打开 A/B 实验", null, (_, _) => ShowMainWindow("ab"));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            var app = Application.Current;
            app?.Dispatcher.Invoke(() =>
            {
                Dispose();
                Application.Current.MainWindow?.Close();
                app.Shutdown();
            });
        });
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowMainWindow();

        _icon = icon;
    }

    public static void Dispose()
    {
        var icon = _icon;
        _icon = null;
        if (icon is null)
            return;
        icon.Visible = false;
        icon.Dispose();
    }

    /// <summary>最小化进托盘时的提示，每次会话只弹一次。</summary>
    public static void ShowMinimizedHint()
    {
        if (!IsEnabled || _minimizeHintShown)
            return;
        _minimizeHintShown = true;
        NotifyRaw("FPS 帧律仍在运行", "已最小化到系统托盘。" + HotkeyHint);
    }

    /// <summary>优化 / 实验完成通知（受设置开关控制）。</summary>
    public static void NotifyComplete(string title, string message)
    {
        if (!IsEnabled || !SettingsService.Current.NotifyOnComplete)
            return;
        NotifyRaw(title, message);
    }

    private static void NotifyRaw(string title, string message)
    {
        var app = Application.Current;
        if (app is null)
            return;
        app.Dispatcher.Invoke(() =>
        {
            EnsureCreated();
            if (_icon is null)
                return;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            // ShowBalloonTip 的 timeout 参数在新系统上由 OS 决定，传最小值即可。
            _icon.ShowBalloonTip(1000);
        });
    }

    private static void ShowMainWindow(string? navigateTo = null)
    {
        var app = Application.Current;
        if (app is null)
            return;
        app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is not MainWindow win)
                return;
            win.RestoreFromTray();
            if (navigateTo is not null)
                win.NavigateTo(navigateTo);
        });
    }
}
