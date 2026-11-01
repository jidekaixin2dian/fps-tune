using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 前台窗口的全屏判定结果。<see cref="Unknown"/> 表示这一轮读不到可靠信息，
/// 调用方必须保持现状——把读失败当成「不是全屏」会在切换瞬间误还原。
/// </summary>
internal enum ForegroundScreenState
{
    Unknown,
    NotFullscreen,
    FullscreenBorderless,
    FullscreenExclusive,
}

/// <summary>像素矩形（与 Win32 RECT 同构，测试可直接构造）。</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

/// <summary>
/// 前台窗口探测的最小接口。把所有 Win32 调用隔离在这里，
/// 于是 <see cref="FullscreenDetector"/> 的判定逻辑可以完全脱离真实窗口做单测。
/// </summary>
internal interface IForegroundProbe
{
    nint GetForegroundWindow();
    nint GetShellWindow();
    nint GetDesktopWindow();

    /// <summary>前台窗口所属进程的可执行名（形如 cs2.exe）。读不到返回 false。</summary>
    bool TryGetProcessName(nint window, out string processName);

    /// <summary>窗口真实边界：优先 DWM 扩展边框，失败回退 GetWindowRect。</summary>
    bool TryGetWindowBounds(nint window, out PixelRect bounds);

    /// <summary>窗口所在显示器的物理矩形与其 GDI 设备名（形如 \\.\DISPLAY1）。</summary>
    bool TryGetMonitor(nint window, out PixelRect bounds, out string gdiDeviceName);

    /// <summary>SHQueryUserNotificationState 原始值（3 = QUNS_RUNNING_D3D_FULL_SCREEN）。</summary>
    bool TryGetNotificationState(out int state);

    /// <summary>DWM 隐藏窗口（UWP 幽灵窗）——它们的矩形不可信。</summary>
    bool IsCloaked(nint window);

    string? ClassNameOf(nint window);
}

/// <summary>真实 Win32 实现。只在产品运行时构造；测试一律注入假探针。</summary>
internal sealed class Win32ForegroundProbe : IForegroundProbe
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;

    public nint GetForegroundWindow() => GetForegroundWindowNative();
    public nint GetShellWindow() => GetShellWindowNative();
    public nint GetDesktopWindow() => GetDesktopWindowNative();

    public bool TryGetProcessName(nint window, out string processName)
    {
        processName = "";
        try
        {
            _ = GetWindowThreadProcessId(window, out var raw);
            var pid = (int)raw;
            if (pid <= 0 || pid == Environment.ProcessId) return false;
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return false;
            processName = name + ".exe";
            return true;
        }
        catch
        {
            // 受保护进程（反作弊）取名字会抛：如实返回读不到，由判定层当成 Unknown。
            return false;
        }
    }

    public bool TryGetWindowBounds(nint window, out PixelRect bounds)
    {
        bounds = default;
        if (DwmGetWindowAttributeRect(window, DwmwaExtendedFrameBounds, out var dwm, Marshal.SizeOf<NativeRect>()) == 0)
        {
            bounds = dwm.ToPixelRect();
            return bounds.Width > 0 && bounds.Height > 0;
        }
        if (!GetWindowRect(window, out var win)) return false;
        bounds = win.ToPixelRect();
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryGetMonitor(nint window, out PixelRect bounds, out string gdiDeviceName)
    {
        bounds = default;
        gdiDeviceName = "";
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == 0) return false;
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        bounds = info.rcMonitor.ToPixelRect();
        gdiDeviceName = info.szDevice ?? "";
        return bounds.Width > 0 && bounds.Height > 0 && gdiDeviceName.Length > 0;
    }

    public bool TryGetNotificationState(out int state)
    {
        state = 0;
        try
        {
            return SHQueryUserNotificationState(out state) == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsCloaked(nint window)
        => DwmGetWindowAttributeInt(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    public string? ClassNameOf(nint window)
    {
        try
        {
            var buffer = new char[256];
            var length = GetClassName(window, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left, top, right, bottom;
        public readonly PixelRect ToPixelRect() => new(left, top, right, bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetForegroundWindowNative();

    [DllImport("user32.dll", EntryPoint = "GetShellWindow")]
    private static extern nint GetShellWindowNative();

    [DllImport("user32.dll", EntryPoint = "GetDesktopWindow")]
    private static extern nint GetDesktopWindowNative();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] buffer, int maxCount);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(nint window, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(nint window, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
