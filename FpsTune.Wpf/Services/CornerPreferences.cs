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
    public CornerPreferences Resolve(string overviewMode)
    {
        var p = Normalize();
        return p.Preset switch
        {
            "classic" => new("classic", overviewMode == "console" ? 6 : 12,
                overviewMode == "console" ? 5 : 8, overviewMode == "console" ? 4 : 8, 3),
            "large" => new(),
            _ => p
        };
    }
    internal static void Apply()
    {
        if (Application.Current is not { } app) return;
        var p = (SettingsService.Current.Corners ?? new()).Resolve(SettingsService.Current.OverviewMode);
        app.Resources["RadiusCard"] = new CornerRadius(p.Card);
        app.Resources["RadiusControl"] = new CornerRadius(p.Control);
        app.Resources["RadiusNav"] = new CornerRadius(p.Nav);
        app.Resources["RadiusChip"] = new CornerRadius(p.Chip);
    }
}
