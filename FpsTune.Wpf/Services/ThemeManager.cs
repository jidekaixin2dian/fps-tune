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
        // 控制台风格：镀铬层（标题栏/页签栏）高通透，整窗极光从所有区域透上来；
        // 内容表面保留足够不透明度保证文字可读。
        SetBrush("SidebarBackgroundBrush", WithAlpha(t.Sidebar, resolved == "light" ? (byte)0x7A : (byte)0x52));
        SetBrush("SurfaceBrush", WithAlpha(t.Surface, resolved == "light" ? (byte)0xE8 : (byte)0xC4));
        SetBrush("SurfaceAltBrush", WithAlpha(t.SurfaceAlt, resolved == "light" ? (byte)0xEC : (byte)0xCC));
        SetBrush("ElevatedBrush", WithAlpha(t.Elevated, resolved == "light" ? (byte)0xF0 : (byte)0xD8));
        SetBrush("InputBackgroundBrush", WithAlpha(t.Input, resolved == "light" ? (byte)0xEA : (byte)0xCC));
        SetBrush("BorderBrush", t.Border);
        SetBrush("BorderHoverBrush", t.BorderHover);
        // 窗口外框专用: 比卡片边框深一档, 保证圆角描边在两种主题下都清晰可辨
        SetBrush("WindowBorderBrush", t.WindowBorder);
        SetBrush("TextPrimaryBrush", t.TextPrimary);
        SetBrush("TextSecondaryBrush", t.TextSecondary);
        SetBrush("TextMutedBrush", t.TextMuted);
        SetBrush("PrimaryBrush", t.Primary);
        SetBrush("PrimaryHoverBrush", t.PrimaryHover);
        SetBrush("OnPrimaryBrush", t.OnPrimary);
        SetBrush("AccentBrush", t.Accent);
        // 选中态软底：强调色 13% 透明铺底，深/浅主题下都刚好托住文字不抢层级
        SetBrush("AccentSoftBrush", WithAlpha(t.Accent, resolved == "light" ? (byte)0x20 : (byte)0x24));
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
            // 浅色控制台：纸面冷白，顶部一线冷色渐入
            backdrop = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Parse("#D8E7F3"), 0),
                    new GradientStop(Parse("#E6EEF6"), 0.5),
                    new GradientStop(Parse("#EDF2F8"), 1)
                }
            };
        }
        else
        {
            // 深色控制台：近黑底，顶部一线青色微光
            backdrop = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Parse("#08131C"), 0),
                    new GradientStop(Parse("#05070A"), 0.55),
                    new GradientStop(Parse("#05070A"), 1)
                }
            };
        }

        backdrop.Freeze();
        var layers = auroraEnabled ? BuildAurora(resolved) : null;
        CurrentAuroraBrushes = layers;

        // 控制台风格无投影：平面 + 发丝分割线；保留资源键，低配与否都置空
        app.Resources["CardShadowEffect"] = null;

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
                    Color.FromArgb(0x34, 0x22, 0xD3, 0xEE),
                    Color.FromArgb(0x16, 0x0E, 0xA5, 0xC2)),
                Radial(new Point(1.02, 0.42), 0.78, 0.9,
                    Color.FromArgb(0x26, 0x3B, 0x82, 0xF6),
                    Color.FromArgb(0x0E, 0x2C, 0x5C, 0xC8)),
                Radial(new Point(0.42, 1.08), 0.94, 0.64,
                    Color.FromArgb(0x22, 0x14, 0xA5, 0xB4),
                    Color.FromArgb(0x0C, 0x0F, 0x7A, 0x85)));
        }

        // 深色控制台：压得更低的青蓝光场，贴着近黑底走
        return new AuroraBrushSet(
            Radial(new Point(0.48, 0.02), 0.92, 0.8,
                Color.FromArgb(0x42, 0x22, 0xD3, 0xEE),
                Color.FromArgb(0x1C, 0x1E, 0xA9, 0xC2)),
            Radial(new Point(1.02, 0.42), 0.78, 0.9,
                Color.FromArgb(0x34, 0x3B, 0x82, 0xF6),
                Color.FromArgb(0x14, 0x2B, 0x5E, 0xC9)),
            Radial(new Point(0.42, 1.08), 0.94, 0.64,
                Color.FromArgb(0x28, 0x2D, 0xD4, 0xBF),
                Color.FromArgb(0x10, 0x1F, 0x8F, 0x8B)));
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
        public Color PrimaryHover { get; }
        public Color OnPrimary { get; }
        public Color Accent { get; }
        public Color Danger { get; }
        public Color Warning { get; }
        public Color Ok { get; }

        public Palette(
            string appBackground, string sidebar, string surface, string surfaceAlt,
            string elevated, string input, string border, string borderHover, string windowBorder,
            string textPrimary, string textSecondary, string textMuted,
            string primary, string primaryHover, string onPrimary, string accent,
            string danger, string warning, string ok)
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
            PrimaryHover = Parse(primaryHover);
            OnPrimary = Parse(onPrimary);
            Accent = Parse(accent);
            Danger = Parse(danger);
            Warning = Parse(warning);
            Ok = Parse(ok);
        }
    }

    // 控制台配色：近黑底 · 电光青动作 · 蓝色数据；文字三级灰阶
    private static readonly Palette DarkPalette = new(
        "#05070A", "#05070A", "#0A0E14", "#0E141C", "#131B25", "#080B10",
        "#1B2431", "#2E3D52", "#2A3648", "#E8EEF5", "#9AA7BA", "#66738A",
        "#3B82F6", "#67E8F9", "#062A30", "#22D3EE", "#F87171", "#F2B75C", "#34D399");

    private static readonly Palette LightPalette = new(
        "#EDF2F8", "#EDF2F8", "#FFFFFF", "#F5F8FC", "#EBF0F7", "#F4F7FB",
        "#D9E2EC", "#B9C8D9", "#9DAEC2", "#14202E", "#3A4A61", "#5D6B84",
        "#2563EB", "#0E7490", "#FFFFFF", "#0891B2", "#DC2626", "#B45309", "#059669");
}
