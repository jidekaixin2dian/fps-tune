namespace FpsTune.Wpf.Services;

/// <summary>
/// 全屏判定（不注入、零驱动）：一个前台窗口 + 三条独立信号，
/// 主判据吃「无边框全屏」，副判据吃「独占全屏」。
///
/// 依据：竞品 game 加加 GPPMagicGlassRT.dll 同时导入了 SHQueryUserNotificationState
/// 与 DwmGetWindowAttribute，正是这套组合；1U 的 HikariGameObserver 只看前台进程、不判全屏。
///
/// 设计红线：读不到可靠信息时返回 <see cref="ForegroundScreenState.Unknown"/>，
/// 绝不返回 NotFullscreen——Unknown 不改变已有状态，否则全屏切换瞬间会被误还原。
/// </summary>
internal sealed class FullscreenDetector(IForegroundProbe probe)
{
    /// <summary>把 GDI 设备名映射成显示器身份；测试与特殊拓扑可覆盖。</summary>
    internal static Func<string, string?>? DisplayIdentityOverride;

    /// <summary>与显示器矩形比对的容差（像素）。投影/DPI 缩放会产生 1-2px 误差。</summary>
    internal const int Tolerance = 2;

    /// <summary>QUNS_RUNNING_D3D_FULL_SCREEN。</summary>
    internal const int RunningD3DFullScreen = 3;

    /// <summary>桌面外壳窗口的类名：它们是全屏大小的窗口，但绝不代表游戏全屏。</summary>
    private static readonly string[] ShellClassNames =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame"];

    /// <summary>判定当前前台状态，并回传窗口所在显示器的身份与前台进程名。</summary>
    internal ForegroundScreenState Evaluate(out string? displayIdentity, out string? processName)
    {
        displayIdentity = null;
        processName = null;

        var window = probe.GetForegroundWindow();
        if (window == 0) return ForegroundScreenState.Unknown;
        if (window == probe.GetShellWindow() || window == probe.GetDesktopWindow())
            return ForegroundScreenState.NotFullscreen;
        if (probe.ClassNameOf(window) is { } className && Array.IndexOf(ShellClassNames, className) >= 0)
            return ForegroundScreenState.NotFullscreen;
        if (probe.IsCloaked(window))
            return ForegroundScreenState.NotFullscreen;

        if (!probe.TryGetProcessName(window, out var name) || string.IsNullOrWhiteSpace(name))
            return ForegroundScreenState.Unknown;
        processName = name;

        if (!probe.TryGetMonitor(window, out var monitor, out var device))
            return ForegroundScreenState.Unknown;

        var state = ForegroundScreenState.NotFullscreen;
        if (probe.TryGetWindowBounds(window, out var bounds) && Covers(bounds, monitor))
        {
            state = ForegroundScreenState.FullscreenBorderless;
        }
        else if (probe.TryGetNotificationState(out var notification))
        {
            // QUNS_BUSY(2) 会在演示与部分 UWP 全屏下返回，不能当全屏；只认 3。
            state = notification == RunningD3DFullScreen
                ? ForegroundScreenState.FullscreenExclusive
                : ForegroundScreenState.NotFullscreen;
        }
        else
        {
            // 矩形没铺满、shell 又答不出状态：两条信号都不全，保持现状。
            return ForegroundScreenState.Unknown;
        }

        if (state == ForegroundScreenState.NotFullscreen) return state;

        // 只有确认全屏时才解析显示器身份：这一步要枚举显示拓扑，别放进每轮的必经路径。
        displayIdentity = ResolveIdentity(device);
        return displayIdentity is null ? ForegroundScreenState.Unknown : state;
    }

    /// <summary>窗口边界是否覆盖显示器矩形（允许 <see cref="Tolerance"/> 像素误差）。</summary>
    internal static bool Covers(PixelRect window, PixelRect monitor)
        => Math.Abs(window.Left - monitor.Left) <= Tolerance
           && Math.Abs(window.Top - monitor.Top) <= Tolerance
           && window.Width >= monitor.Width - Tolerance
           && window.Height >= monitor.Height - Tolerance;

    private static string? ResolveIdentity(string gdiDeviceName)
    {
        if (DisplayIdentityOverride is { } custom) return custom(gdiDeviceName);
        try
        {
            var displays = DisplayTopology.GetDisplays();
            var match = displays.FirstOrDefault(d => SameDevice(d.GdiDeviceName, gdiDeviceName));
            return match is null ? null : DisplayTopology.Identity(match);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CCD 的 source 名与 MONITORINFOEX.szDevice 都是 \\.\DISPLAYn，但大小写与前缀写法不保证一致。</summary>
    private static bool SameDevice(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
           || string.Equals(StripDevicePrefix(left), StripDevicePrefix(right), StringComparison.OrdinalIgnoreCase);

    private static string StripDevicePrefix(string value)
        => value.StartsWith(@"\\.\", StringComparison.Ordinal) ? value[4..] : value;
}
