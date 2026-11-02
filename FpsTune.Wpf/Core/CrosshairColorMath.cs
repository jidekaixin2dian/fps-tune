namespace FpsTune.Wpf.Core;

/// <summary>
/// 准星调色盘用到的颜色换算（纯函数，可单测）：<c>#RRGGBB</c> ↔ HSV。
/// 调色盘与十六进制输入框共用这一份换算，避免两边各写一套取整规则后对不上。
/// </summary>
public static class CrosshairColorMath
{
    /// <summary>预设色：通用准星色（默认绿、纯红/品红/青/白/黑等），点一下即写入设置。</summary>
    public static readonly string[] Presets =
    [
        "#00FF66", "#39FF14", "#FFFFFF", "#000000", "#FF3B30",
        "#FF00E5", "#00E5FF", "#FFD60A", "#FF9500", "#AF52DE"
    ];

    /// <summary>解析 6 位十六进制（可带 #）；非法返回 false，不做任何默认值兜底。</summary>
    public static bool TryParse(string? text, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        var trimmed = (text ?? "").Trim();
        if (trimmed.StartsWith('#')) trimmed = trimmed[1..];
        if (trimmed.Length != 6) return false;
        for (var i = 0; i < trimmed.Length; i++)
            if (!Uri.IsHexDigit(trimmed[i])) return false;
        rgb = (Convert.ToByte(trimmed[0..2], 16), Convert.ToByte(trimmed[2..4], 16), Convert.ToByte(trimmed[4..6], 16));
        return true;
    }

    /// <summary>RGB → HSV；色相 0–360、饱和度 0–100、明度 0–100。</summary>
    public static (double Hue, double Saturation, double Value) ToHsv((byte R, byte G, byte B) rgb)
    {
        double r = rgb.R / 255d, g = rgb.G / 255d, b = rgb.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
        }
        if (hue < 0) hue += 360;
        var saturation = max <= 0 ? 0 : delta / max;
        return (Math.Round(hue, 2), Math.Round(saturation * 100, 2), Math.Round(max * 100, 2));
    }

    /// <summary>HSV → <c>#RRGGBB</c>；超出范围先夹紧，色相按 360 取模。</summary>
    public static string FromHsv(double hue, double saturation, double value)
    {
        var h = ((hue % 360) + 360) % 360;
        var s = Math.Clamp(saturation, 0, 100) / 100d;
        var v = Math.Clamp(value, 0, 100) / 100d;
        var chroma = v * s;
        var x = chroma * (1 - Math.Abs((h / 60d) % 2 - 1));
        var m = v - chroma;
        (double R, double G, double B) channel = h switch
        {
            < 60 => (chroma, x, 0),
            < 120 => (x, chroma, 0),
            < 180 => (0, chroma, x),
            < 240 => (0, x, chroma),
            < 300 => (x, 0, chroma),
            _ => (chroma, 0, x)
        };
        return $"#{To255(channel.R + m):X2}{To255(channel.G + m):X2}{To255(channel.B + m):X2}";
    }

    private static int To255(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
}
