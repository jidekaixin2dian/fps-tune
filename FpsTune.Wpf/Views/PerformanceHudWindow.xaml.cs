using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class PerformanceHudWindow : Window
{
    private const int ExtendedStyle = -20, Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
    private const int Hotkey = 0x4F02;
    private readonly PerformanceSessionService _session;
    private readonly MetricsSampler _metrics;
    private readonly DispatcherTimer _ticker;
    private MetricSample? _sample;
    private long _sampleReceived;
    private nint _handle;
    private bool _registered, _through;
    private string? _interactionError;
    internal Func<bool>? RecoveryAvailableOverride;
    internal bool IsClickThrough => _through;

    internal PerformanceHudWindow(PerformanceSessionService session, MetricsSampler metrics)
    {
        _session = session; _metrics = metrics;
        InitializeComponent();
        _ticker = new() { Interval = TimeSpan.FromSeconds(UiPerformance.LowSpec ? 3 : 1) };
        _ticker.Tick += (_, _) => Refresh();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(_handle, ExtendedStyle, GetWindowLongPtr(_handle, ExtendedStyle) | ToolWindow | NoActivate);
            HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
            if (UserDataPaths.RootOverride is null) _registered = RegisterHotKey(_handle, Hotkey, 0x4003, 0x48); // Ctrl+Alt+H, no repeats.
        };
        Loaded += (_, _) =>
        {
            _metrics.Sampled += OnSample; _session.StateChanged += Refresh;
            UpdateVisibility(); Refresh();
            if (UserDataPaths.RootOverride is null) PlaceInWorkArea();
        };
        DpiChanged += (_, _) => _ = Dispatcher.BeginInvoke(PlaceInWorkArea);
        IsVisibleChanged += (_, _) => UpdateVisibility();
        StateChanged += (_, _) => UpdateVisibility();
        Closed += (_, _) =>
        {
            _ticker.Stop(); _metrics.Sampled -= OnSample; _session.StateChanged -= Refresh;
            _session.RequestLiveFps(false);
            if (_registered) UnregisterHotKey(_handle, Hotkey);
            _handle = 0;
        };
    }
    private void UpdateVisibility()
    {
        var visible = IsLoaded && IsVisible && WindowState != WindowState.Minimized;
        _session.RequestLiveFps(visible);
        if (visible) _ticker.Start(); else _ticker.Stop();
        App.UpdateLiveMetrics();
    }
    private void OnSample(MetricSample sample)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        _sample = sample; _sampleReceived = Environment.TickCount64; Refresh();
    }
    internal void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(Refresh); return; }
        var current = Environment.TickCount64 - _sampleReceived <= Math.Max(5000, _metrics.Interval.TotalMilliseconds * 2) ? _sample : null;
        CpuText.Text = "CPU  " + Percent(current?.CpuPercent);
        GpuText.Text = "GPU  " + Percent(current?.GpuPercent);
        var vram = current?.VramUsedBytes is { } bytes ? $"{bytes / (1024d * 1024 * 1024):F1} GiB" : "—";
        MemoryText.Text = Str.T("Str.HudMemory", Percent(current?.MemoryPercent), vram);
        var fps = _session.CurrentLiveFps;
        FpsText.Text = fps is null ? "—" : fps.Fps.ToString("F0");
        LowText.Text = fps is null ? "—" : fps.P1Low.ToString("F0");
        ScopeText.Text = _interactionError ?? (_through ? Str.T("Str.HudLocked") : _session.IsRunning
            ? Str.T("Str.HudRecording", _session.Conditions?.GameName ?? _session.SessionName) : Str.T("Str.HudIdle"));
        CaptureStatus.Text = !_session.IsRunning ? Str.T("Str.HudRecordForFps")
            : _session.CurrentFpsUnavailableReasonKey is { } reason ? Str.T(reason)
            : fps is null ? Str.T("Str.HudWaitingFrames") : Str.T("Str.HudWindow", fps.WindowSeconds.ToString("F1"), fps.Samples);
    }
    private static string Percent(double? value) => value is { } number && double.IsFinite(number) ? $"{number:F0}%" : "—";
    internal bool SetClickThrough(bool enabled)
    {
        if (enabled && !(RecoveryAvailableOverride?.Invoke() ?? (_registered || TrayService.IsTrayVisible)))
        { _interactionError = Str.T("Str.HudRecoveryUnavailable"); Refresh(); return false; }
        if (_handle == 0) return false;
        var style = GetWindowLongPtr(_handle, ExtendedStyle);
        SetWindowLongPtr(_handle, ExtendedStyle, enabled ? style | Transparent : style & ~Transparent);
        if (((GetWindowLongPtr(_handle, ExtendedStyle) & Transparent) != 0) != enabled)
        { _interactionError = Str.T("Str.HudThroughFailed"); Refresh(); return false; }
        _interactionError = null; _through = enabled; Refresh(); return true;
    }
    private void Through_Click(object sender, RoutedEventArgs e) => SetClickThrough(true);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Pin_Changed(object sender, RoutedEventArgs e) { if (Pin is not null) Topmost = Pin.IsChecked == true; }
    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((ReferenceEquals(e.OriginalSource, HudCaption) || ReferenceEquals(e.OriginalSource, DragArea))
            && e.LeftButton == MouseButtonState.Pressed && !_through)
        { DragMove(); PlaceInWorkArea(); }
    }
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam == Hotkey) { SetClickThrough(!_through); handled = true; }
        if (msg == 0x007E) _ = Dispatcher.BeginInvoke(PlaceInWorkArea); // display topology changed
        return 0;
    }
    private void PlaceInWorkArea()
    {
        if (_handle == 0 || !GetWindowRect(_handle, out var rect)) return;
        var monitor = MonitorFromWindow(_handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var x = Math.Clamp(rect.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - (rect.Right - rect.Left)));
        var y = Math.Clamp(rect.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - (rect.Bottom - rect.Top)));
        SetWindowPos(_handle, 0, x, y, 0, 0, 0x15); // No activate, no size, no z-order change.
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out RectI rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
