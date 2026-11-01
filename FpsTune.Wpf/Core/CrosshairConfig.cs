using System.Runtime.InteropServices;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>外置准星配置（持久化 + 行为纯函数，可单测）。</summary>
public sealed record CrosshairConfig(
    bool Enabled,
    string Style,
    string Color,
    double Size,
    double Gap,
    double Thickness,
    double Opacity,
    bool AdaptiveHide)
{
    public static readonly string[] Styles = ["cross", "dot", "crossdot", "circle"];

    public static CrosshairConfig FromSettings()
    {
        var s = SettingsService.Current;
        return new(s.CrosshairEnabled, NormalizeStyle(s.CrosshairStyle), NormalizeColor(s.CrosshairColor),
            Math.Clamp(s.CrosshairSize, 2, 64), Math.Clamp(s.CrosshairGap, 0, 40),
            Math.Clamp(s.CrosshairThickness, 1, 16), Math.Clamp(s.CrosshairOpacity, 10, 100),
            s.CrosshairAdaptiveHide);
    }

    /// <summary>样式白名单归一；非法回 cross。</summary>
    public static string NormalizeStyle(string style)
        => Styles.Contains(style, StringComparer.OrdinalIgnoreCase) ? style.ToLowerInvariant() : "cross";

    /// <summary>#RRGGBB 归一；非法回 #00FF66。</summary>
    public static string NormalizeColor(string color)
    {
        var trimmed = (color ?? "").Trim();
        if (trimmed.Length == 0) return "#00FF66";
        if (!trimmed.StartsWith('#')) trimmed = "#" + trimmed;
        return trimmed.Length == 7 && Uri.IsHexDigit(trimmed[1]) && Uri.IsHexDigit(trimmed[2])
            && Uri.IsHexDigit(trimmed[3]) && Uri.IsHexDigit(trimmed[4]) && Uri.IsHexDigit(trimmed[5])
            && Uri.IsHexDigit(trimmed[6]) ? trimmed.ToUpperInvariant() : "#00FF66";
    }

    /// <summary>
    /// AW 战术据枪：按住右键（机瞄/倍镜）时隐藏准星；本应用窗口在前台时也隐藏（避免挡设置）。
    /// </summary>
    public bool ShouldHide(bool rightButtonHeld, bool selfForeground)
        => selfForeground || (AdaptiveHide && rightButtonHeld);
}

/// <summary>右键按住状态（GetAsyncKeyState，输入监听，零驱动零内存读取）。</summary>
internal static class NativeInputState
{
    internal static Func<bool>? RightButtonOverride { get; set; }

    /// <summary>右键当前是否按住。</summary>
    internal static bool RightButtonHeld()
    {
        if (RightButtonOverride is not null) return RightButtonOverride();
        return (GetAsyncKeyState(0x02) & 0x8000) != 0;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int virtualKey);
}
