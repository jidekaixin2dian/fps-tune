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
    bool AdaptiveHide,
    string DisplayMode = "always",
    string AdsMode = "hold",
    bool HideOnAds = true,
    string FireTrigger = CrosshairTriggers.DefaultFire,
    string AdsTrigger = CrosshairTriggers.DefaultAds,
    string SyncTrigger = "",
    int ShowDelayMs = 0,
    int HideDelayMs = 0)
{
    public static readonly string[] Styles = ["cross", "dot", "crossdot", "circle"];

    /// <summary>显示方式白名单：一直显示 / 开火时显示 / 战术据枪。</summary>
    public static readonly string[] DisplayModes = ["always", "fire", "ads"];

    /// <summary>开镜方式白名单：按住 / 点击切换。</summary>
    public static readonly string[] AdsModes = ["hold", "toggle"];

    public static CrosshairConfig FromSettings()
    {
        var s = SettingsService.Current;
        return new(s.CrosshairEnabled, NormalizeStyle(s.CrosshairStyle), NormalizeColor(s.CrosshairColor),
            Math.Clamp(s.CrosshairSize, 2, 64), Math.Clamp(s.CrosshairGap, 0, 40),
            Math.Clamp(s.CrosshairThickness, 1, 16), Math.Clamp(s.CrosshairOpacity, 10, 100),
            s.CrosshairAdaptiveHide,
            NormalizeDisplayMode(s.CrosshairDisplayMode), NormalizeAdsMode(s.CrosshairAdsMode), s.CrosshairHideOnAds,
            CrosshairTriggers.Normalize(s.CrosshairFireTrigger, CrosshairTriggers.DefaultFire),
            CrosshairTriggers.Normalize(s.CrosshairAdsTrigger, CrosshairTriggers.DefaultAds),
            CrosshairTriggers.NormalizeOrEmpty(s.CrosshairSyncTrigger),
            Math.Clamp(s.CrosshairShowDelayMs, 0, 1000), Math.Clamp(s.CrosshairHideDelayMs, 0, 1000));
    }

    /// <summary>样式白名单归一；非法回 cross。</summary>
    public static string NormalizeStyle(string style)
        => Styles.Contains(style, StringComparer.OrdinalIgnoreCase) ? style.ToLowerInvariant() : "cross";

    /// <summary>显示方式白名单归一；非法回 always（与老配置一致）。</summary>
    public static string NormalizeDisplayMode(string? mode)
        => DisplayModes.Contains((mode ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            ? mode!.Trim().ToLowerInvariant() : "always";

    /// <summary>开镜方式白名单归一；非法回 hold。</summary>
    public static string NormalizeAdsMode(string? mode)
        => AdsModes.Contains((mode ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            ? mode!.Trim().ToLowerInvariant() : "hold";

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

    public CrosshairDisplayMode DisplayModeKind => DisplayMode switch
    {
        "fire" => CrosshairDisplayMode.Fire,
        "ads" => CrosshairDisplayMode.Ads,
        _ => CrosshairDisplayMode.Always,
    };

    public CrosshairAdsMode AdsModeKind
        => string.Equals(AdsMode, "toggle", StringComparison.OrdinalIgnoreCase)
            ? CrosshairAdsMode.Toggle : CrosshairAdsMode.Hold;

    /// <summary>
    /// 开镜时是否隐藏。一直显示模式沿用老字段 <see cref="AdaptiveHide"/>（老用户升级后行为不变），
    /// 其余模式读新字段。
    /// </summary>
    public bool EffectiveHideOnAds
        => DisplayModeKind == CrosshairDisplayMode.Always ? AdaptiveHide : HideOnAds;

    /// <summary>
    /// 按住模式下的隐藏判定（老接口，供既有回归用例锁语义）：
    /// <c>always</c> + <see cref="AdaptiveHide"/> 等价于原来的「按住右键隐藏」。
    /// </summary>
    public bool ShouldHide(bool rightButtonHeld, bool selfForeground)
        => selfForeground || (EffectiveHideOnAds && rightButtonHeld);
}

/// <summary>右键按住状态（兼容入口；实际读取在 <see cref="CrosshairTriggers"/>）。</summary>
internal static class NativeInputState
{
    internal static Func<bool>? RightButtonOverride { get; set; }

    /// <summary>右键当前是否按住。</summary>
    internal static bool RightButtonHeld() => CrosshairTriggers.IsHeld("MouseRight");
}
