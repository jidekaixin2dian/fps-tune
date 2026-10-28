namespace FpsTune.Wpf.Services;

public sealed record HudAppearance(string Layout = "strip", double Transparency = 35, string Position = "top-center")
{
    public HudAppearance Normalize() => new(Layout is "card" or "strip" ? Layout : "strip",
        double.IsFinite(Transparency) ? Math.Clamp(Transparency, 0, 100) : 35,
        Position is "top-left" or "top-right" or "top-center" ? Position : "top-center");
}
