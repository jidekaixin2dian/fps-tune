using System.Globalization;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>一次采样结果：读数 + 判读结论；读不到时 Reading 为 null 且带原因。</summary>
internal sealed record GpuTelemetrySnapshot(
    GpuReading? Reading,
    GpuVerdict Verdict,
    DateTimeOffset At,
    string? UnavailableMessage)
{
    internal static GpuTelemetrySnapshot Unavailable(string message) =>
        new(null, GpuVerdict.Unavailable, DateTimeOffset.Now, message);
}

/// <summary>
/// 显卡实时状态：一次 nvidia-smi 查询拿到利用率、显存、温度、功耗、频率、风扇与降频原因。
/// 全程只读；没有 nvidia-smi（A 卡/Intel、精简驱动）时如实说明读不到。
/// </summary>
internal static class GpuTelemetryService
{
    /// <summary>测试注入：直接给一份快照，避免用例依赖真实显卡。</summary>
    internal static Func<GpuTelemetrySnapshot>? ReadOverride { get; set; }

    internal static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(2);

    private static GpuTelemetrySnapshot? _cached;

    internal static GpuTelemetrySnapshot Read(bool allowCache = true)
    {
        if (ReadOverride is { } injected) return injected();
        if (allowCache && _cached is { } cached && DateTimeOffset.Now - cached.At < CacheWindow) return cached;

        GpuTelemetrySnapshot snapshot;
        try
        {
            var result = NativeSystem.Run("nvidia-smi.exe", GpuTelemetry.Query, "--format=csv,noheader,nounits");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                snapshot = GpuTelemetrySnapshot.Unavailable(Str.T("Str.GpuUnavailableNoSmi",
                    PrivacyScrub.Sanitize(result.Error.Trim())));
            }
            else
            {
                var reading = GpuTelemetry.Parse(result.Output.Split('\n')[0]);
                snapshot = reading is null
                    ? GpuTelemetrySnapshot.Unavailable(Str.T("Str.GpuUnavailableParse"))
                    : new GpuTelemetrySnapshot(reading, GpuTelemetry.Evaluate(reading), DateTimeOffset.Now, null);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("gpu-telemetry", ex);
            snapshot = GpuTelemetrySnapshot.Unavailable(PrivacyScrub.Sanitize(ex.Message));
        }

        _cached = snapshot;
        return snapshot;
    }

    /// <summary>格式：显存 "3.6 / 11.9 GB"；读不到写"—"。</summary>
    internal static string FormatMemory(GpuReading reading)
        => reading is { MemoryUsedMb: { } used, MemoryTotalMb: { } total }
            ? Str.T("Str.GpuMemoryFormat", (used / 1024d).ToString("0.0", CultureInfo.InvariantCulture),
                (total / 1024d).ToString("0.0", CultureInfo.InvariantCulture))
            : "—";

    internal static string FormatPercent(double? value) => value is { } number
        ? number.ToString("0", CultureInfo.InvariantCulture) + "%"
        : "—";

    internal static string FormatNumber(double? value, string unit) => value is { } number
        ? number.ToString("0", CultureInfo.InvariantCulture) + unit
        : "—";

    /// <summary>降频原因按位展开成人类可读的短词；没有降频返回空数组。</summary>
    internal static IReadOnlyList<string> ThrottleReasons(GpuReading reading)
    {
        var reasons = new List<string>();
        if ((reading.ThrottleMask & GpuReading.PowerCap) != 0) reasons.Add(Str.T("Str.GpuThrottlePowerCap"));
        if ((reading.ThrottleMask & GpuReading.ThermalSw) != 0) reasons.Add(Str.T("Str.GpuThrottleThermal"));
        if ((reading.ThrottleMask & GpuReading.ThermalHw) != 0) reasons.Add(Str.T("Str.GpuThrottleThermalHw"));
        if ((reading.ThrottleMask & GpuReading.HwSlowdown) != 0) reasons.Add(Str.T("Str.GpuThrottleSlowdown"));
        if ((reading.ThrottleMask & GpuReading.PowerBrake) != 0) reasons.Add(Str.T("Str.GpuThrottleBrake"));
        return reasons;
    }
}
