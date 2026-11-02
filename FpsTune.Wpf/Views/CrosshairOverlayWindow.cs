using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 外置准星覆盖窗：全屏透明、置顶、点击穿透、不激活不进任务栏；
/// 跟随前台窗口所在显示器居中；AW 战术据枪=按住右键自动隐藏。
/// </summary>
public sealed class CrosshairOverlayWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Grid _root = new();
    private CrosshairConfig _config = new(false, "cross", "#00FF66", 12, 4, 2, 90, true);

    public CrosshairOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Content = _root;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => RefreshFrame();
        SourceInitialized += (_, _) => MakeClickThrough();
        ApplyConfig(CrosshairConfig.FromSettings());
    }

    public void ApplyConfig(CrosshairConfig config)
    {
        _config = config;
        Opacity = config.Opacity / 100.0;
        _root.Children.Clear();
        // 图形与设置页预览共用同一份构建逻辑，颜色/样式/尺寸不会出现两套。
        _root.Children.Add(CrosshairShape.Build(config));
        RefreshFrame();
    }

    public void StartPolling() => _timer.Start();

    public void StopPolling() => _timer.Stop();

    /// <summary>每帧：AW 隐藏判定 + 跟随前台显示器居中。</summary>
    private void RefreshFrame()
    {
        if (!IsLoaded) return;
        var adsHeld = NativeInputState.RightButtonHeld();
        var selfForeground = ForegroundBelongsToSelf();
        Visibility = _config.ShouldHide(adsHeld, selfForeground) ? Visibility.Hidden : Visibility.Visible;
        PositionOnForegroundMonitor();
    }

    private static bool ForegroundBelongsToSelf()
    {
        var hwnd = GetForegroundWindow();
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>把窗口铺满前台窗口所在显示器（物理像素 SetWindowPos，绕开 DPI 换算），准星由布局居中。</summary>
    private void PositionOnForegroundMonitor()
    {
        var hwnd = GetForegroundWindow();
        var monitor = hwnd != nint.Zero
            ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
            : MonitorFromWindow(new nint(0), MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        SetWindowPos(Handle, nint.Zero,
            info.rcMonitor.Left, info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top,
            SWP_NOACTIVATE | SWP_NOZORDER);
    }

    private void MakeClickThrough()
    {
        var style = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE,
            style | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private nint Handle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    // 物理像素摆放（跨显示器 DPI 下 WPF DIP 换算不可靠）
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint hwnd, int index, int style);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    private const uint SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
