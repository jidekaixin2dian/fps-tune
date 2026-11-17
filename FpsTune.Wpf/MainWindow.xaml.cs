using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

/// <summary>
/// 主窗口的外壳：窗口构造、DWM 圆角、标题栏按钮、整体缩放、以及窗口消息入口。
/// <para>
/// 这个类按职责分成六个 partial 文件，各自的边界写在文件头：
/// <list type="bullet">
/// <item>本文件：外壳——构造顺序、窗口装饰、<c>WndProc</c> 分发、缩放快捷键。</item>
/// <item><c>MainWindow.Navigation</c>：页面创建与缓存、侧栏选中、路由、各页面入口。</item>
/// <item><c>MainWindow.Notifications</c>：更新 / 社区 / 新手引导通知卡与启动检查。</item>
/// <item><c>MainWindow.Hotkeys</c>：全局热键的注册、注销与 WM_HOTKEY 分发。</item>
/// <item><c>MainWindow.Lifetime</c>：关闭流程、托盘常驻与最小化去向。</item>
/// <item><c>MainWindow.Commands</c> / <c>MainWindow.Hud</c> / <c>MainWindow.GameTuning</c>：命令面板、悬浮窗选项、游戏调校入口。</item>
/// </list>
/// 新增功能先判断属于哪一块；放不进任何一块时，多半说明它该有自己的视图，而不是塞回窗口。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    // 使用非分层窗口和 WindowChrome，保留 DWM 原生窗口动画与圆角。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    private HwndSource? _hwndSource;
    private bool _savingSessionOnExit;

    public MainWindow()
    {
        InitializeComponent();
        _navButtons = BuildNavButtons();
        InitializeCommandPalette();
        HookGameContextService();

        PageHost.Content = GetPage("home");
        // 托盘图标随应用启动常驻(与最小化设置无关); 设置只控制最小化行为
        if (UserDataPaths.RootOverride is null) TrayService.EnsureCreated();

        // 版本号唯一来源：程序集（编译自 Directory.Build.props）
        var ver = "v" + UpdateService.DisplayVersion;
        TitleVersionText.Text = ver;
        AdminStatusText.Text = AdminHelper.IsAdministrator() ? Str.T("Str.AdminMode") : Str.T("Str.NormalUser");

        var theme = SettingsService.Current.ThemeMode;
        if (string.IsNullOrWhiteSpace(theme))
            theme = "dark";
        ThemeManager.SetMode(theme);
        RefreshWallpaper();

        StateChanged += OnStateChanged;
        UpdateCaptionState();
        ChromeGrid.SizeChanged += (_, _) => UpdateRootClip();
        Closing += OnMainClosing;
        Closed += (_, _) =>
        {
            App.AutomaticRecording?.Dispose();
            App.CloseHud();
            _updateLifetime.Cancel();
            _updateCard?.Cancel();
            TrayService.Dispose();
            ReleaseHotkeys();
        };
        Loaded += (_, _) => { if (UserDataPaths.RootOverride is null) _ = CheckStartupUpdateAsync(); };
        // 全局滚轮穿透：滑块/下拉框不再吞掉滚轮，页面能一路滚到底（各页面都受益）。
        Views.Controls.WheelPassthrough.Attach(this);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);

        var pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwndSource!.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        if (UserDataPaths.RootOverride is null) ApplyHotkeyRegistration();
    }

    /// <summary>窗口消息入口：关窗按钮先走应用自己的「关闭去向」，热键消息转交热键宿主。</summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0112 /* WM_SYSCOMMAND */ && ((long)wParam & 0xfff0) == 0xf060 /* SC_CLOSE */)
        {
            handled = true;
            _ = Dispatcher.BeginInvoke(RequestUserClose);
            return nint.Zero;
        }
        if (msg == WM_HOTKEY && HandleHotkeyMessage(wParam))
        {
            handled = true;
        }
        return nint.Zero;
    }

    /// <summary>
    /// 把窗口内容裁剪到自绘圆角内：标题栏/侧栏的不透明背景否则会溢出到
    /// 圆角外的方角区域，盖掉边框线并形成白色方角。
    /// </summary>
    private void UpdateRootClip()
    {
        var radius = WindowState == WindowState.Maximized ? 0 : 8;
        ChromeGrid.Clip = new RectangleGeometry(
            new Rect(0, 0, ChromeGrid.ActualWidth, ChromeGrid.ActualHeight), radius, radius);
    }

    internal void RefreshWallpaper()
    {
        WallpaperBackdrop.Visibility = Visibility.Collapsed;
        WallpaperBackdrop.Source = null;
        if (!SettingsService.Current.WallpaperEnabled) return;
        try
        {
            WallpaperBackdrop.Source = WallpaperService.Load();
            WallpaperBackdrop.Opacity = WallpaperService.NormalizeOpacity(SettingsService.Current.WallpaperOpacity);
            if (WallpaperBackdrop.Source is not null) WallpaperBackdrop.Visibility = Visibility.Visible;
        }
        catch (Exception) { /* An unavailable personal backdrop does not block opening the application. */ }
    }

    private void CaptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;
        switch (action)
        {
            case "min":
                SystemCommands.MinimizeWindow(this);
                break;
            case "max":
                if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
                else SystemCommands.MaximizeWindow(this);
                break;
            case "close":
                RequestUserClose();
                break;
        }
    }

    /// <summary>Ctrl +/-/0 整体缩放：范围 0.85–1.25，再小会把 13 号字压到不可读。</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        double? step = e.Key switch
        {
            System.Windows.Input.Key.OemPlus or System.Windows.Input.Key.Add => 0.05,
            System.Windows.Input.Key.OemMinus or System.Windows.Input.Key.Subtract => -0.05,
            System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0 => null,
            _ => double.MaxValue,
        };
        if (step is { } s && s == double.MaxValue) return;
        var current = step is null ? 1d : Math.Clamp(UiZoom.ScaleX + step.Value, 0.85, 1.25);
        UiZoom.ScaleX = UiZoom.ScaleY = Math.Round(current, 2);
        e.Handled = true;
    }
}
