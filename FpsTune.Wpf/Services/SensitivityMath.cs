namespace FpsTune.Wpf.Services;

/// <summary>
/// 灵敏度换算（参考实现的灵敏度调校助手）：纯计算，不碰系统。
/// 每个引擎每"数一格"转过的角度是固定的（yaw，单位度），所以
/// cm/360 = (360 / (灵敏度 × yaw)) / DPI × 2.54。
/// </summary>
public static class SensitivityMath
{
    /// <summary>常见引擎的 yaw 常量（度/每 count）。</summary>
    public static readonly IReadOnlyList<(string Key, double Yaw)> Yaws =
    [
        ("source", 0.022),  // CS2 / CS:GO / Apex / Overwatch
        ("valorant", 0.07), // VALORANT
    ];

    /// <summary>一圈 360 度需要移动的厘米数；参数非法返回 null。</summary>
    public static double? Cm360(double dpi, double sensitivity, double yaw)
    {
        if (!(dpi > 0) || !(sensitivity > 0) || !(yaw > 0) || double.IsInfinity(dpi) || double.IsInfinity(sensitivity))
            return null;
        var counts = 360.0 / (sensitivity * yaw);
        return counts / dpi * 2.54;
    }

    /// <summary>eDPI = DPI × 灵敏度。</summary>
    public static double? Edpi(double dpi, double sensitivity)
        => dpi > 0 && sensitivity > 0 && double.IsFinite(dpi) && double.IsFinite(sensitivity)
            ? dpi * sensitivity
            : null;

    /// <summary>把一条灵敏度换算成另一个 yaw 下的等价档位（保持手感一致）。</summary>
    public static double? Equivalent(double sensitivity, double fromYaw, double toYaw)
        => sensitivity > 0 && fromYaw > 0 && toYaw > 0 && double.IsFinite(sensitivity)
            ? sensitivity * fromYaw / toYaw
            : null;
}
