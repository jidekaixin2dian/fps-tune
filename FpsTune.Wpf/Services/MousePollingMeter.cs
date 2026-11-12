using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 鼠标报告频率实测：订阅原始输入（Raw Input），统计连续移动上报之间的间隔，
/// 再交给 <see cref="PollingRateMath"/> 换算。只读输入事件，不安装驱动、不改任何设置。
///
/// 订阅需要一个真实存在的顶层窗口句柄：message-only 窗口（HWND_MESSAGE）收不到原始输入，
/// 所以这里临时建一个 1×1、屏幕外、不进任务栏、不激活的小窗，测完立刻销毁。
/// 必须在本进程的 UI 线程上调用（要建窗口）。
/// </summary>
internal sealed class MousePollingMeter : IDisposable
{
    /// <summary>测试注入：直接给一份读数，避免用例依赖真实鼠标。</summary>
    internal static Func<TimeSpan, Task<PollingRateReading>>? MeasureOverride { get; set; }

    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const ushort MouseMoveAbsolute = 0x01;
    private const uint RidevInputSink = 0x00000100;

    private readonly List<double> _intervals = [];
    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly Stopwatch _clock = new();
    private long _lastTimestamp;
    private bool _registered;

    private MousePollingMeter()
    {
        _window = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Opacity = 0,
            Content = null
        };
        // 不设 Owner：测试宿主与主窗口都可能不存在；独立小窗更不容易被一起关掉。
        if (Application.Current?.MainWindow is { } main && !ReferenceEquals(main, _window) && main.IsLoaded)
            _window.Owner = main;
        _window.Show();
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle)
                  ?? throw new InvalidOperationException(Str.T("Str.InputQueueMeasureFailed", "no-window"));
    }

    /// <summary>采集一段时间的移动上报，返回读数。窗口内没有移动时会返回「样本不足」。</summary>
    internal static async Task<PollingRateReading> MeasureAsync(TimeSpan window, CancellationToken token = default)
    {
        if (MeasureOverride is { } custom) return await custom(window).ConfigureAwait(true);

        using var meter = new MousePollingMeter();
        meter.Start();
        try
        {
            await Task.Delay(window, token).ConfigureAwait(true);
        }
        finally
        {
            meter.Stop();
        }

        return PollingRateMath.Analyze(meter._intervals);
    }

    private void Start()
    {
        var device = new RawInputDevice
        {
            UsagePage = 1,      // HID_USAGE_PAGE_GENERIC
            Usage = 2,          // HID_USAGE_GENERIC_MOUSE
            Flags = RidevInputSink,
            Target = _source.Handle
        };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new InvalidOperationException(Str.T("Str.InputQueueMeasureFailed",
                Marshal.GetLastWin32Error().ToString()));
        _clock.Start();
        _lastTimestamp = 0;
        _registered = true;
        _source.AddHook(OnMessage);
    }

    private void Stop()
    {
        if (!_registered) return;
        _registered = false;
        _source.RemoveHook(OnMessage);
        _clock.Stop();
        // 注销订阅：把目标窗口设成 0 并清掉 INPUTSINK，避免窗口销毁后残留注册。
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0, Target = IntPtr.Zero };
        _ = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput || !TryReadMove(lParam)) return IntPtr.Zero;

        var now = _clock.ElapsedTicks;
        if (_lastTimestamp != 0)
        {
            var intervalMs = (now - _lastTimestamp) * 1000d / Stopwatch.Frequency;
            _intervals.Add(intervalMs);
        }
        _lastTimestamp = now;
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>只认「相对移动」的鼠标事件：绝对坐标（绘图板/远程桌面）的频率没有参考意义。</summary>
    private static bool TryReadMove(IntPtr rawInput)
    {
        var size = 0u;
        _ = GetRawInputData(rawInput, RidInput, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RawInputHeader>());
        if (size == 0 || size > 4096) return false;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInput, RidInput, buffer, ref size, (uint)Marshal.SizeOf<RawInputHeader>()) == uint.MaxValue)
                return false;
            if (Marshal.ReadInt32(buffer, 0) != RimTypeMouse) return false;

            // RAWINPUTHEADER 在 x64 上是 24 字节（4+4+8+8），鼠标结构紧跟在后面。
            var headerSize = Marshal.SizeOf<RawInputHeader>();
            var flags = (ushort)Marshal.ReadInt16(buffer, headerSize);
            if ((flags & MouseMoveAbsolute) != 0) return false;

            var x = Marshal.ReadInt32(buffer, headerSize + 12);
            var y = Marshal.ReadInt32(buffer, headerSize + 16);
            return x != 0 || y != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    public void Dispose()
    {
        Stop();
        _source.Dispose();
        try { _window.Close(); } catch (InvalidOperationException) { /* 已经关掉 */ }
    }
}
