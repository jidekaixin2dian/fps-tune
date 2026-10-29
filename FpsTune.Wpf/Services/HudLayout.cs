namespace FpsTune.Wpf.Services;

internal sealed record HudLayout(double Width, int Columns, double FpsSize, bool Compact)
{
    internal static HudLayout For(string layout) => layout switch
    {
        "card" => new(300, 2, 24, false),
        "strip" => new(560, 6, 22, false),
        _ => new(420, 4, 18, true)
    };
}
