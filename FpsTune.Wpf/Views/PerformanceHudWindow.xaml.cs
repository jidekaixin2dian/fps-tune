using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private bool _closePressed;
    private string? _interactionError;
    private HudAppearance _appearance = new();
    internal Func<bool>? RecoveryAvailableOverride;
    // Physical pixels; an offscreen work area keeps native placement tests away from the user's apps.
    internal (int Left, int Top, int Right, int Bottom)? RecoveryWorkAreaOverride;
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
            if (UserDataPaths.RootOverride is null) PositionInWorkArea(true);
        };
        DpiChanged += (_, _) => { if (UserDataPaths.RootOverride is null) _ = Dispatcher.BeginInvoke(PlaceInWorkArea); };
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
        MemoryText.Text = Str.T("Str.HudRam", Percent(current?.MemoryPercent));
        VramText.Text = Str.T("Str.HudVram", vram);
        var fps = _session.CurrentLiveFps;
        FpsText.Text = fps is null ? "—" : fps.Fps.ToString("F0");
        LowText.Text = fps is null ? "—" : fps.P1Low.ToString("F0");
        ScopeText.Text = _interactionError ?? (_through ? Str.T("Str.HudLocked") : _session.IsRunning
            ? Str.T("Str.HudRecording", _session.Conditions?.GameName ?? _session.SessionName) : Str.T("Str.HudIdle"));
        ScopeText.Visibility = _appearance.Layout == "strip" && _interactionError is null ? Visibility.Collapsed : Visibility.Visible;
        CaptureStatus.Text = !_session.IsRunning ? Str.T("Str.HudRecordForFps")
            : _session.CurrentFpsUnavailableReasonKey is { } reason ? Str.T(reason)
            : fps is null ? Str.T("Str.HudWaitingFrames") : Str.T("Str.HudWindow", fps.WindowSeconds.ToString("F1"), fps.Samples);
    }
    private static string Percent(double? value) => value is { } number && double.IsFinite(number) ? $"{number:F0}%" : "—";
    internal void ApplyAppearance(HudAppearance appearance)
    {
        _appearance = appearance.Normalize();
        var strip = _appearance.Layout == "strip";
        Width = strip ? 720 : 360; HudMetrics.Columns = strip ? 6 : 2;
        HudBackdrop.Opacity = 1 - _appearance.Transparency / 100;
        ScopeText.Visibility = strip ? Visibility.Collapsed : Visibility.Visible;
        var clear = _appearance.Transparency >= 75;
        foreach (var text in MetricText(HudMetrics).Append(CaptureStatus).Append(ScopeText))
        {
            if (clear) text.Foreground = Brushes.White;
            else text.SetResourceReference(Control.ForegroundProperty, text == CaptureStatus || text == ScopeText ? "TextSecondaryBrush" : "TextPrimaryBrush");
        }
        HudMetrics.Effect = clear ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = .9 } : null;
        if (IsLoaded)
        {
            UpdateLayout();
            if (UserDataPaths.RootOverride is null || RecoveryWorkAreaOverride is not null) PositionInWorkArea(true);
        }
    }
    private static IEnumerable<TextBlock> MetricText(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) yield return text;
            foreach (var nested in MetricText(child)) yield return nested;
        }
    }
    private void ShowInteractionState()
    {
        HudActions.Visibility = _through ? Visibility.Collapsed : Visibility.Visible;
        LockedHint.Visibility = _through ? Visibility.Visible : Visibility.Collapsed;
    }
    internal bool SetClickThrough(bool enabled)
    {
        if (enabled && !(RecoveryAvailableOverride?.Invoke() ?? (_registered || TrayService.IsTrayVisible)))
        { _interactionError = Str.T("Str.HudRecoveryUnavailable"); Refresh(); return false; }
        if (_handle == 0) return false;
        var style = GetWindowLongPtr(_handle, ExtendedStyle);
        if (((style & Transparent) != 0) == enabled)
        { _interactionError = null; _through = enabled; ShowInteractionState(); Refresh(); return true; }
        SetWindowLongPtr(_handle, ExtendedStyle, enabled ? style | Transparent : style & ~Transparent);
        if (((GetWindowLongPtr(_handle, ExtendedStyle) & Transparent) != 0) != enabled)
        { _interactionError = Str.T("Str.HudThroughFailed"); Refresh(); return false; }
        _interactionError = null; _through = enabled; ShowInteractionState(); Refresh(); return true;
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
        if (msg == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE: deliver the first click without taking focus.
        if (msg is 0x001f or 0x0215) _closePressed = false;
        if (msg == 0x0201) _closePressed = HitCloseButton(lParam);
        if (msg == 0x0202)
        {
            var close = _closePressed && HitCloseButton(lParam); _closePressed = false;
            if (close)
            {
                handled = true;
                _ = Dispatcher.BeginInvoke(() => { if (_handle != 0) Close(); });
                return 0;
            }
        }
        if (msg == 0x0312 && wParam == Hotkey) { SetClickThrough(!_through); handled = true; }
        if (msg == 0x007E && UserDataPaths.RootOverride is null) _ = Dispatcher.BeginInvoke(PlaceInWorkArea); // display topology changed
        return 0;
    }
    private bool HitCloseButton(nint coordinates)
    {
        if (_through || !CloseHudButton.IsVisible || !CloseHudButton.IsEnabled) return false;
        var point = new Point((short)(coordinates.ToInt64() & 0xffff), (short)((coordinates.ToInt64() >> 16) & 0xffff));
        if (HwndSource.FromHwnd(_handle)?.CompositionTarget is not { } target) return false;
        point = target.TransformFromDevice.Transform(point);
        return new Rect(CloseHudButton.TranslatePoint(new Point(), this), CloseHudButton.RenderSize).Contains(point);
    }
    internal bool RestoreForInteraction()
    {
        if (!SetClickThrough(false)) return false;
        if (UserDataPaths.RootOverride is not null && RecoveryWorkAreaOverride is null) return true;
        return PositionInWorkArea(true);
    }
    private void PlaceInWorkArea() => PositionInWorkArea(false);
    private bool PositionInWorkArea(bool restore)
    {
        if (_handle == 0 || !GetWindowRect(_handle, out var rect)) return false;
        RectI work;
        if (restore && RecoveryWorkAreaOverride is { } testArea)
            work = new() { Left = testArea.Left, Top = testArea.Top, Right = testArea.Right, Bottom = testArea.Bottom };
        else
        {
            var reference = restore && Application.Current.MainWindow is { } main
                ? new WindowInteropHelper(main).Handle : _handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(reference, 2), ref info)) return false;
            work = info.Work;
        }
        var right = Math.Max(work.Left, work.Right - (rect.Right - rect.Left));
        var bottom = Math.Max(work.Top, work.Bottom - (rect.Bottom - rect.Top));
        var preferredX = _appearance.Position == "top-left" ? work.Left + 16
            : _appearance.Position == "top-right" ? right - 16 : work.Left + (right - work.Left) / 2;
        var x = Math.Clamp(restore ? preferredX : rect.Left, work.Left, right);
        var y = Math.Clamp(restore ? work.Top + 16 : rect.Top, work.Top, bottom);
        // Explicit recovery raises within the selected pin group without activation or resizing.
        return SetWindowPos(_handle, restore ? (Topmost ? -1 : -2) : 0, x, y, 0, 0, restore ? 0x51u : 0x15u);
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
