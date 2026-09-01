using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
        // 保留足够不透明度保证文字可读, 同时让整窗极光能够穿透各层表面。
        SetBrush("SidebarBackgroundBrush", WithAlpha(t.Sidebar, resolved == "light" ? (byte)0xC6 : (byte)0xA0));
        SetBrush("SurfaceBrush", WithAlpha(t.Surface, resolved == "light" ? (byte)0xEA : (byte)0xD9));
        SetBrush("SurfaceAltBrush", WithAlpha(t.SurfaceAlt, resolved == "light" ? (byte)0xEE : (byte)0xDF));
        SetBrush("ElevatedBrush", WithAlpha(t.Elevated, resolved == "light" ? (byte)0xF0 : (byte)0xE5));
        SetBrush("InputBackgroundBrush", WithAlpha(t.Input, resolved == "light" ? (byte)0xED : (byte)0xE2));
        SetBrush("BorderBrush", t.Border);
        SetBrush("BorderHoverBrush", t.BorderHover);
        // 窗口外框专用: 比卡片边框深一档, 保证圆角描边在两种主题下都清晰可辨
        SetBrush("WindowBorderBrush", t.WindowBorder);
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

    // 页面底色只提供基础对比; 极光由主窗口的三层固定渐变覆盖,
    // 不放进页面背景——否则会随页面滑入动画平移, 且侧栏区域出现"无光割裂"。
    private static void ApplyBackdrop(string resolved)
    {
        var app = Application.Current;
        if (app is null)
            return;

        bool auroraEnabled = SettingsService.Current.AuroraEnabled;

        LinearGradientBrush backdrop;
        if (resolved == "light")
        {
            // 浅色主题的氛围要"看得见"：底色带明确蓝调（顶部更深、往下渐浅）；
            // 表面保留高不透明度，靠少量冷色透光与投影形成层次。
            backdrop = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Parse("#D9E6FB"), 0),
                    new GradientStop(Parse("#E2E9F5"), 0.55),
                    new GradientStop(Parse("#E9EEF7"), 1)
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
        }

        backdrop.Freeze();
        var layers = auroraEnabled ? BuildAurora(resolved) : null;
        CurrentAuroraBrushes = layers;

        // 卡片阴影: 低配模式整体关闭(阴影是 WPF 里最贵的视觉), 收敛后的尺寸
        // 保证不超出 24px 页边距、不会被窗口圆角裁切成"断裂"
        if (UiPerformance.LowSpec)
            app.Resources["CardShadowEffect"] = null;
        else
            app.Resources["CardShadowEffect"] = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.20,
                Color = Color.FromRgb(0, 0, 0)
            };

        app.Resources["AppBackdropBrush"] = backdrop;
        // 页面根全部引用 Transparent 版 AppBackgroundBrush(App.xaml), 窗口层背景透出
        app.Resources["AppBackgroundBrush"] = TransparentBrush();
    }

    /// <summary>当前主题的三层整窗极光刷子; 光场关闭时为 null。</summary>
    internal static AuroraBrushSet? CurrentAuroraBrushes { get; private set; }

    private static Brush TransparentBrush()
    {
        var b = new SolidColorBrush(Colors.Transparent);
        b.Freeze();
        return b;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static AuroraBrushSet BuildAurora(string resolved)
    {
        if (resolved == "light")
        {
            return new AuroraBrushSet(
                Radial(new Point(0.48, 0.02), 0.92, 0.8,
                    Color.FromArgb(0x3A, 0x4F, 0x5D, 0xF5),
                    Color.FromArgb(0x16, 0x3E, 0x5C, 0xB8)),
                Radial(new Point(1.02, 0.42), 0.78, 0.9,
                    Color.FromArgb(0x28, 0x28, 0xB9, 0xC7),
                    Color.FromArgb(0x10, 0x2D, 0x7F, 0xA8)),
                Radial(new Point(0.42, 1.08), 0.94, 0.64,
                    Color.FromArgb(0x22, 0x3A, 0x7E, 0xAC),
                    Color.FromArgb(0x0C, 0x2B, 0x58, 0x80)));
        }

        return new AuroraBrushSet(
            Radial(new Point(0.48, 0.02), 0.92, 0.8,
                Color.FromArgb(0x70, 0x5A, 0x5B, 0xFF),
                Color.FromArgb(0x2E, 0x52, 0x6F, 0xB8)),
            Radial(new Point(1.02, 0.42), 0.78, 0.9,
                Color.FromArgb(0x49, 0x38, 0xD6, 0xD8),
                Color.FromArgb(0x1E, 0x2F, 0x9C, 0xB5)),
            Radial(new Point(0.42, 1.08), 0.94, 0.64,
                Color.FromArgb(0x38, 0x5D, 0x70, 0xBE),
                Color.FromArgb(0x18, 0x2E, 0x61, 0x90)));
    }

    private static RadialGradientBrush Radial(Point center, double radiusX, double radiusY, Color centerColor, Color midColor)
    {
        var brush = new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = radiusX,
            RadiusY = radiusY,
            GradientStops =
            {
                new GradientStop(centerColor, 0),
                new GradientStop(midColor, 0.54),
                new GradientStop(Color.FromArgb(0x00, midColor.R, midColor.G, midColor.B), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    internal sealed class AuroraBrushSet
    {
        public RadialGradientBrush Main { get; }
        public RadialGradientBrush Side { get; }
        public RadialGradientBrush Reflection { get; }

        public AuroraBrushSet(RadialGradientBrush main, RadialGradientBrush side, RadialGradientBrush reflection)
        {
            Main = main;
            Side = side;
            Reflection = reflection;
        }
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
        public Color WindowBorder { get; }
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
            string elevated, string input, string border, string borderHover, string windowBorder,
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
            WindowBorder = Parse(windowBorder);
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
        "#232B38", "#364356", "#3A4759", "#EDF1F7", "#A6B1C2", "#7E8BA0",
        "#4DA3FF", "#818CF8", "#F87171", "#F2B75C", "#34D399");

    private static readonly Palette LightPalette = new(
        "#EFF2F7", "#FBFCFE", "#FFFFFF", "#F4F6FA", "#FFFFFF", "#F7F9FC",
        "#E3E8F0", "#CBD4E1", "#AAB6C9", "#1A2233", "#3E4A61", "#5D6B85",
        "#2563EB", "#4F46E5", "#DC2626", "#B45309", "#059669");
}
