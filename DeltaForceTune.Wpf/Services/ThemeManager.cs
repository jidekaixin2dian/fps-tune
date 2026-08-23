using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeltaForceTune.Wpf.Services;

public static class ThemeManager
{
    public static string CurrentMode { get; private set; } = "dark";

    private static DispatcherTimer? _timer;

    public static void Initialize()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            if (CurrentMode == "system")
                Apply("system");
        };
        _timer.Start();
    }

    public static void SetMode(string mode)
    {
        CurrentMode = mode;
        Apply(mode);
    }

    public static void Apply(string mode)
    {
        var resolved = mode == "system" ? ResolveSystemTheme() : mode;
        var t = resolved == "light" ? LightPalette : DarkPalette;

        AnimateBrush("AppBackgroundBrush", t.AppBackground);
        AnimateBrush("SidebarBackgroundBrush", t.Sidebar);
        AnimateBrush("SurfaceBrush", t.Surface);
        AnimateBrush("SurfaceAltBrush", t.SurfaceAlt);
        AnimateBrush("ElevatedBrush", t.Elevated);
        AnimateBrush("InputBackgroundBrush", t.Input);
        AnimateBrush("BorderBrush", t.Border);
        AnimateBrush("BorderHoverBrush", t.BorderHover);
        AnimateBrush("TextPrimaryBrush", t.TextPrimary);
        AnimateBrush("TextSecondaryBrush", t.TextSecondary);
        AnimateBrush("TextMutedBrush", t.TextMuted);
        AnimateBrush("PrimaryBrush", t.Primary);
        AnimateBrush("AccentBrush", t.Accent);
        AnimateBrush("DangerBrush", t.Danger);
        AnimateBrush("WarningBrush", t.Warning);
        AnimateBrush("OkBrush", t.Ok);
        Application.Current.Resources["PrimaryColor"] = t.Primary;
        Application.Current.Resources["AccentColor"] = t.Accent;
    }

    private static void AnimateBrush(string key, Color target)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush)
        {
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(320)));
        }
        else
        {
            Application.Current.Resources[key] = new SolidColorBrush(target);
        }
    }

    private static string ResolveSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i && i == 1)
                return "light";
            return "dark";
        }
        catch
        {
            return "dark";
        }
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private sealed class Palette
    {
        public Color AppBackground { get; }
        public Color Sidebar { get; }
        public Color Surface { get; }
        public Color SurfaceAlt { get; }
        public Color Elevated { get; }
        public Color Input { get; }
        public Color Border { get; }
        public Color BorderHover { get; }
        public Color TextPrimary { get; }
        public Color TextSecondary { get; }
        public Color TextMuted { get; }
        public Color Primary { get; }
        public Color Accent { get; }
        public Color Danger { get; }
        public Color Warning { get; }
        public Color Ok { get; }

        public Palette(
            string appBackground, string sidebar, string surface, string surfaceAlt,
            string elevated, string input, string border, string borderHover,
            string textPrimary, string textSecondary, string textMuted,
            string primary, string accent, string danger, string warning, string ok)
        {
            AppBackground = Parse(appBackground);
            Sidebar = Parse(sidebar);
            Surface = Parse(surface);
            SurfaceAlt = Parse(surfaceAlt);
            Elevated = Parse(elevated);
            Input = Parse(input);
            Border = Parse(border);
            BorderHover = Parse(borderHover);
            TextPrimary = Parse(textPrimary);
            TextSecondary = Parse(textSecondary);
            TextMuted = Parse(textMuted);
            Primary = Parse(primary);
            Accent = Parse(accent);
            Danger = Parse(danger);
            Warning = Parse(warning);
            Ok = Parse(ok);
        }
    }

    private static readonly Palette DarkPalette = new(
        "#09090B", "#0C0C0E", "#111113", "#131316", "#18181B", "#0B0B0D",
        "#26262A", "#3F3F46", "#F4F4F5", "#A1A1AA", "#71717A",
        "#10B981", "#22D3EE", "#F87171", "#FBBF24", "#34D399");

    private static readonly Palette LightPalette = new(
        "#F4F4F5", "#FAFAFA", "#FFFFFF", "#F4F4F5", "#FFFFFF", "#FAFAFA",
        "#E4E4E7", "#D4D4D8", "#18181B", "#52525B", "#71717A",
        "#059669", "#0891B2", "#DC2626", "#D97706", "#059669");
}
