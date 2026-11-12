using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 显卡实时状态卡：利用率、显存、温度、功耗、频率、风扇与降频原因，并给一句结论。
/// 只读 nvidia-smi；页面可见时每 2 秒采一次，离开页面即停（不在后台常驻轮询）。
/// </summary>
public partial class GpuLivePanel : UserControl
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _timer;
    private bool _busy;

    public GpuLivePanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Refresh();
            SyncTimer();
        };
        Unloaded += (_, _) => StopTimer();
    }

    /// <summary>按开关起停轮询；离开页面时无论开关如何都停。</summary>
    private void SyncTimer()
    {
        if (AutoRefreshToggle?.IsChecked != true || !IsLoaded)
        {
            StopTimer();
            return;
        }

        _timer ??= new DispatcherTimer { Interval = Interval };
        if (_timer.IsEnabled) return;
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Tick -= Timer_Tick;
        _timer.Stop();
    }

    private void Timer_Tick(object? sender, EventArgs e) => Refresh();

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e) => SyncTimer();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>采一次并刷新界面；internal 供用例直接驱动。</summary>
    internal void Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snapshot = GpuTelemetryService.Read(allowCache: false);
            Apply(snapshot);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Apply(GpuTelemetrySnapshot snapshot)
    {
        if (GpuNameText is null) return;
        if (snapshot.Reading is not { } reading)
        {
            GpuNameText.Text = Str.T("Str.GpuLiveNameUnavailable");
            GpuVerdictText.Text = Str.T(GpuTelemetry.VerdictKey(GpuVerdict.Unavailable));
            GpuMeasuredText.Text = "";
            foreach (var text in new[] { UtilText, MemoryText, TempText, PowerText, ClockSmText, ClockMemText, FanText })
                text.Text = "—";
            ThrottleText.Text = "—";
            AdviceText.Text = "";
            StatusText.Text = snapshot.UnavailableMessage ?? Str.T("Str.GpuUnavailableParse");
            return;
        }

        GpuNameText.Text = reading.Name;
        GpuVerdictText.Text = Str.T(GpuTelemetry.VerdictKey(snapshot.Verdict));
        GpuMeasuredText.Text = Str.T("Str.GpuLiveMeasuredAt", snapshot.At.ToString("HH:mm:ss"));
        UtilText.Text = GpuTelemetryService.FormatPercent(reading.UtilizationPercent);
        MemoryText.Text = GpuTelemetryService.FormatMemory(reading);
        TempText.Text = GpuTelemetryService.FormatNumber(reading.TemperatureC, " °C");
        PowerText.Text = reading.PowerW is { } power
            ? reading.PowerLimitW is { } limit
                ? Str.T("Str.GpuLivePowerFormat",
                    power.ToString("0", CultureInfo.InvariantCulture), limit.ToString("0", CultureInfo.InvariantCulture))
                : power.ToString("0", CultureInfo.InvariantCulture) + " W"
            : "—";
        ClockSmText.Text = GpuTelemetryService.FormatNumber(reading.ClockSmMhz, " MHz");
        ClockMemText.Text = GpuTelemetryService.FormatNumber(reading.ClockMemMhz, " MHz");
        FanText.Text = GpuTelemetryService.FormatPercent(reading.FanPercent);

        var reasons = GpuTelemetryService.ThrottleReasons(reading);
        ThrottleText.Text = reasons.Count == 0 ? Str.T("Str.GpuLiveNoThrottle") : string.Join(" / ", reasons);
        AdviceText.Text = Str.T(GpuTelemetry.AdviceKey(snapshot.Verdict));
        StatusText.Text = "";
    }
}
