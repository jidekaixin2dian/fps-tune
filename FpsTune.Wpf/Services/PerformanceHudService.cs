using System.Windows;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

internal sealed class PerformanceHudService(PerformanceSessionService? session = null, MetricsSampler? metrics = null,
    Action<PerformanceHudWindow>? prepare = null)
{
    internal PerformanceHudWindow? Window { get; private set; }
    internal bool IsVisible => Window is { IsVisible: true, WindowState: not WindowState.Minimized };
    internal void Show()
    {
        if (Window is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show(); App.UpdateLiveMetrics(); return;
        }
        var window = new PerformanceHudWindow(session ?? App.SessionService, metrics ?? App.LiveMetrics);
        if (Application.Current.MainWindow is { } main)
        { window.Left = main.Left + main.ActualWidth - window.Width - 24; window.Top = main.Top + 90; }
        Window = window;
        window.Closed += (_, _) => { Window = null; App.UpdateLiveMetrics(); };
        prepare?.Invoke(window);
        window.Show(); // ShowActivated=false; opening the HUD does not bring the game out of focus.
        App.UpdateLiveMetrics();
    }
    internal void Close() => Window?.Close();
    internal void Unlock() => Window?.SetClickThrough(false);
}
