using System.Windows;

namespace FpsTune.Wpf.Services;

public sealed record CornerPreferences(string Preset = "large", double Card = 18, double Control = 12, double Nav = 12, double Chip = 8)
{
    public CornerPreferences Normalize() => this with
    {
        Preset = Preset is "classic" or "large" or "custom" ? Preset : "large",
        Card = Clamp(Card, 32, 18), Control = Clamp(Control, 24, 12),
        Nav = Clamp(Nav, 24, 12), Chip = Clamp(Chip, 16, 8)
    };
    private static double Clamp(double value, double max, double fallback)
        => double.IsFinite(value) ? Math.Round(Math.Clamp(value, 0, max)) : fallback;
    /// <summary>
    /// 圆角固定为上一版数值：卡片 18 / 控件 12 / 导航 12 / 胶囊 8（焦点环 11）。
    /// 用户要求"以上版本为准、不开放调整"，因此忽略设置里存的偏好值。
    /// </summary>
    internal static void Apply()
    {
        if (Application.Current is not { } app) return;
        var p = new CornerPreferences();
        app.Resources["RadiusCard"] = new CornerRadius(p.Card);
        app.Resources["RadiusControl"] = new CornerRadius(p.Control);
        app.Resources["RadiusFocus"] = new CornerRadius(Math.Max(0, p.Control - 1));
        app.Resources["RadiusNav"] = new CornerRadius(p.Nav);
        app.Resources["RadiusChip"] = new CornerRadius(p.Chip);
    }
}
