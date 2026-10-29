namespace FpsTune.Wpf.Services;

public sealed record HudAppearance(string Layout = "compact", double Transparency = 75, string Position = "top-right")
{
    public HudAppearance Normalize() => new(Layout is "card" or "strip" or "compact" ? Layout : "compact",
        double.IsFinite(Transparency) ? Math.Clamp(Transparency, 0, 100) : 75,
        Position is "top-left" or "top-right" or "top-center" ? Position : "top-right");
}
