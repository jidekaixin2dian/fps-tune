using System.Globalization;

namespace FpsTune.Wpf.Core;

/// <summary>一次显卡遥测读数。取不到的项是 null（[N/A]、[Not Supported] 或没装驱动）。</summary>
internal sealed record GpuReading(
    string Name,
    double? UtilizationPercent,
    double? MemoryUsedMb,
    double? MemoryTotalMb,
    double? TemperatureC,
    double? PowerW,
    double? PowerLimitW,
    double? ClockSmMhz,
    double? ClockMemMhz,
    double? FanPercent,
    ulong ThrottleMask)
{
    internal double? MemoryPercent => MemoryUsedMb is { } used && MemoryTotalMb is > 0 and { } total
        ? 100d * used / total
        : null;

    internal bool ThermalThrottled => (ThrottleMask & (ThermalSw | ThermalHw | HwSlowdown)) != 0;

    internal bool PowerThrottled => (ThrottleMask & (PowerCap | PowerBrake)) != 0;

    internal const ulong PowerCap = 0x04;
    internal const ulong PowerBrake = 0x80;
    internal const ulong ThermalSw = 0x20;
    internal const ulong ThermalHw = 0x40;
    internal const ulong HwSlowdown = 0x08;
}

/// <summary>显卡这边的判读结论：值本身不带文案，界面按 key 取词。</summary>
public enum GpuVerdict
{
    /// <summary>读不到读数（没装 nvidia-smi / A 卡 / 驱动异常）。</summary>
    Unavailable,

    /// <summary>显卡基本空转：没跑游戏，或帧率被别的东西卡住了。</summary>
    Idle,

    /// <summary>显卡吃满：提帧要靠画质、分辨率、DLSS 这些往下压。</summary>
    GpuBound,

    /// <summary>显卡吃得不低（85% 以上），提帧空间有限。</summary>
    GpuBusy,

    /// <summary>显卡没吃满：瓶颈更可能在 CPU、帧率上限或垂直同步。</summary>
    Headroom,

    /// <summary>显存占用偏高。</summary>
    VramHigh,

    /// <summary>显存快满了，最容易表现为卡顿与掉帧。</summary>
    VramCritical,

    /// <summary>温度撞墙降频。</summary>
    ThermalThrottle,

    /// <summary>功耗撞墙降频。</summary>
    PowerThrottle
}

/// <summary>
/// 显卡遥测的解析与判读（纯函数，用例可覆盖全部边界）。
/// 数据来自 nvidia-smi 的一次查询，字段顺序与 <see cref="Query"/> 一一对应。
/// </summary>
internal static class GpuTelemetry
{
    /// <summary>查询字段：顺序即解析顺序，别单独改一处。</summary>
    internal const string Query =
        "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,power.limit," +
        "clocks.sm,clocks.mem,fan.speed,clocks_throttle_reasons.active";

    /// <summary>显存超过这个比例就提醒（再往上会开始掉帧与卡顿）。</summary>
    internal const double VramHighPercent = 90;

    /// <summary>显存到这个比例按"快满了"处理。</summary>
    internal const double VramCriticalPercent = 95;

    /// <summary>显卡利用率到这个数按"吃满"处理：接近 100% 时每一点画质都直接换成帧数。</summary>
    internal const double GpuBoundPercent = 97;

    /// <summary>低于这个利用率且不是空转，就提示瓶颈不在显卡。</summary>
    internal const double HeadroomPercent = 85;

    /// <summary>低于这个利用率视为"没在跑重负载"。</summary>
    internal const double IdlePercent = 5;

    /// <summary>解析一行 csv,noheader,nounits 输出；字段不足或名字为空时返回 null。</summary>
    internal static GpuReading? Parse(string? csvLine)
    {
        var line = (csvLine ?? "").Trim();
        if (line.Length == 0) return null;
        var parts = line.Split(',');
        if (parts.Length < 11) return null;

        var name = parts[0].Trim();
        if (name.Length == 0) return null;
        return new GpuReading(
            name,
            Number(parts[1]),
            Number(parts[2]),
            Number(parts[3]),
            Number(parts[4]),
            Number(parts[5]),
            Number(parts[6]),
            Number(parts[7]),
            Number(parts[8]),
            Number(parts[9]),
            Mask(parts[10]));
    }

    private static double? Number(string text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return null;
        if (value.StartsWith('[') || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number
            : null;
    }

    private static ulong Mask(string text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value.StartsWith('[')) return 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0;
        return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    /// <summary>按优先级给一个结论：显存 > 温度/功耗降频 > 吃满 > 没吃满 > 空转。</summary>
    internal static GpuVerdict Evaluate(GpuReading reading)
    {
        if (reading.MemoryPercent is { } memory)
        {
            if (memory >= VramCriticalPercent) return GpuVerdict.VramCritical;
            if (memory >= VramHighPercent) return GpuVerdict.VramHigh;
        }

        if (reading.ThermalThrottled) return GpuVerdict.ThermalThrottle;
        if (reading.PowerThrottled) return GpuVerdict.PowerThrottle;

        if (reading.UtilizationPercent is not { } utilization) return GpuVerdict.Unavailable;
        if (utilization >= GpuBoundPercent) return GpuVerdict.GpuBound;
        if (utilization <= IdlePercent) return GpuVerdict.Idle;
        return utilization >= HeadroomPercent ? GpuVerdict.GpuBusy : GpuVerdict.Headroom;
    }

    internal static string VerdictKey(GpuVerdict verdict) => verdict switch
    {
        GpuVerdict.VramCritical => "Str.GpuVerdictVramCritical",
        GpuVerdict.VramHigh => "Str.GpuVerdictVramHigh",
        GpuVerdict.ThermalThrottle => "Str.GpuVerdictThermal",
        GpuVerdict.PowerThrottle => "Str.GpuVerdictPower",
        GpuVerdict.GpuBound => "Str.GpuVerdictBound",
        GpuVerdict.GpuBusy => "Str.GpuVerdictBusy",
        GpuVerdict.Headroom => "Str.GpuVerdictHeadroom",
        GpuVerdict.Idle => "Str.GpuVerdictIdle",
        _ => "Str.GpuVerdictUnavailable"
    };

    /// <summary>结论对应的下一步（一句话，给具体动作，不写免责声明）。</summary>
    internal static string AdviceKey(GpuVerdict verdict) => verdict switch
    {
        GpuVerdict.VramCritical or GpuVerdict.VramHigh => "Str.GpuAdviceVram",
        GpuVerdict.ThermalThrottle => "Str.GpuAdviceThermal",
        GpuVerdict.PowerThrottle => "Str.GpuAdvicePower",
        GpuVerdict.GpuBound => "Str.GpuAdviceBound",
        GpuVerdict.GpuBusy => "Str.GpuAdviceBusy",
        GpuVerdict.Headroom => "Str.GpuAdviceHeadroom",
        GpuVerdict.Idle => "Str.GpuAdviceIdle",
        _ => "Str.GpuAdviceUnavailable"
    };
}
