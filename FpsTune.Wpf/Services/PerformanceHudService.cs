using System.Windows;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

internal sealed class PerformanceHudService(PerformanceSessionService? session = null, MetricsSampler? metrics = null,
    Action<PerformanceHudWindow>? prepare = null)
{
    internal HudAppearance Appearance { get; private set; } = (SettingsService.Current.HudAppearance ?? new()).Normalize();
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
        window.ApplyAppearance(Appearance);
        if (Application.Current.MainWindow is { } main)
        { window.Left = main.Left + main.ActualWidth - window.Width - 24; window.Top = main.Top + 90; }
        Window = window;
        window.Closed += (_, _) => { Window = null; App.UpdateLiveMetrics(); };
        prepare?.Invoke(window);
        window.Show(); // ShowActivated=false; opening the HUD does not bring the game out of focus.
        App.UpdateLiveMetrics();
    }
    internal void Close() => Window?.Close();
    internal void PreviewAppearance(HudAppearance appearance)
    {
        Appearance = appearance.Normalize();
        Window?.ApplyAppearance(Appearance);
    }
    internal void Restore()
    {
        Show();
        if (Window is { } window && !window.RestoreForInteraction())
            DialogService.Warning(Str.T("Str.HudTitle"), Str.T("Str.HudRestoreFailed"));
    }
}
