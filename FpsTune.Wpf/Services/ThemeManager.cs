using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

public static class ThemeManager
{
    public static string CurrentMode { get; private set; } = "dark";

    private static bool _hooked;

    public static void Initialize()
    {
        if (_hooked)
            return;
        _hooked = true;

        // 监听系统主题变更事件（代替轮询注册表）；事件在后台线程触发，需回到 UI 线程。
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || CurrentMode != "system")
                return;

            var app = Application.Current;
            if (app is null)
                return;
            app.Dispatcher.Invoke(() => Apply("system"));
        };
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

        SetBrush("AppBackgroundBrush", t.AppBackground);
        SetBrush("SidebarBackgroundBrush", t.Sidebar);
        SetBrush("SurfaceBrush", t.Surface);
        SetBrush("SurfaceAltBrush", t.SurfaceAlt);
        SetBrush("ElevatedBrush", t.Elevated);
        SetBrush("InputBackgroundBrush", t.Input);
        SetBrush("BorderBrush", t.Border);
        SetBrush("BorderHoverBrush", t.BorderHover);
        SetBrush("TextPrimaryBrush", t.TextPrimary);
        SetBrush("TextSecondaryBrush", t.TextSecondary);
        SetBrush("TextMutedBrush", t.TextMuted);
        SetBrush("PrimaryBrush", t.Primary);
        SetBrush("AccentBrush", t.Accent);
        SetBrush("DangerBrush", t.Danger);
        SetBrush("WarningBrush", t.Warning);
        SetBrush("OkBrush", t.Ok);
        Application.Current.Resources["PrimaryColor"] = t.Primary;
        Application.Current.Resources["AccentColor"] = t.Accent;
        ApplyBackdrop(resolved);
    }

    // 页面底色: 顶部带一点蓝调的垂直渐变, 加顶部柔光, 让"素底"有层次
    private static void ApplyBackdrop(string resolved)
    {
        var app = Application.Current;
        if (app is null)
            return;

        LinearGradientBrush backdrop;
        RadialGradientBrush glow;
        if (resolved == "light")
        {
            backdrop = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Parse("#F7FAFF"), 0),
                    new GradientStop(Parse("#EFF2F7"), 0.6),
                    new GradientStop(Parse("#EFF2F7"), 1)
                }
            };
            glow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x22, 0x4F, 0x46, 0xE5), 0),
                    new GradientStop(Color.FromArgb(0x00, 0x4F, 0x46, 0xE5), 1)
                }
            };
        }
        else
        {
            backdrop = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Parse("#121A29"), 0),
                    new GradientStop(Parse("#0A0D12"), 0.6),
                    new GradientStop(Parse("#0A0D12"), 1)
                }
            };
            glow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x30, 0x4D, 0xA3, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x00, 0x4D, 0xA3, 0xFF), 1)
                }
            };
        }

        backdrop.Freeze();
        glow.Freeze();
        app.Resources["AppBackdropBrush"] = backdrop;
        app.Resources["AuroraGlowBrush"] = glow;
    }

    private static void SetBrush(string key, Color target)
    {
        Application.Current.Resources[key] = new SolidColorBrush(target);
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

    // 深空蓝灰底 · 靛青强调；文字三级灰阶
    private static readonly Palette DarkPalette = new(
        "#0A0D12", "#0D1016", "#12161E", "#161B24", "#1C222D", "#10141C",
        "#232B38", "#364356", "#EDF1F7", "#A6B1C2", "#7E8BA0",
        "#4DA3FF", "#818CF8", "#F87171", "#F2B75C", "#34D399");

    private static readonly Palette LightPalette = new(
        "#EFF2F7", "#FBFCFE", "#FFFFFF", "#F4F6FA", "#FFFFFF", "#F7F9FC",
        "#E3E8F0", "#CBD4E1", "#1A2233", "#3E4A61", "#5D6B85",
        "#2563EB", "#4F46E5", "#DC2626", "#B45309", "#059669");
}
