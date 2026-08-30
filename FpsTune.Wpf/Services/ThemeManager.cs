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
        SetBrush("SidebarBackgroundBrush", WithAlpha(t.Sidebar, 0xD9));
        SetBrush("SurfaceBrush", t.Surface);
        SetBrush("SurfaceAltBrush", t.SurfaceAlt);
        SetBrush("ElevatedBrush", t.Elevated);
        SetBrush("InputBackgroundBrush", t.Input);
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

    // 页面底色: 顶部带蓝调的垂直渐变。极光柔光在主窗口的独立层(AuroraLayer),
    // 不放进页面背景——否则会随页面滑入动画平移, 且侧栏区域出现"无光割裂"。
    private static void ApplyBackdrop(string resolved)
    {
        var app = Application.Current;
        if (app is null)
            return;

        bool aurora = SettingsService.Current.AuroraEnabled;

        LinearGradientBrush backdrop;
        RadialGradientBrush? glow = null;
        if (resolved == "light")
        {
            // 浅色主题的氛围要"看得见"：底色带明确蓝调（顶部更深、往下渐浅）；
            // 卡片保持纯白，靠色差与投影形成层次。
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
            if (aurora)
            {
                // 加强版: v1.1.5 原参数为 0x22, 几乎不可感知
                glow = Radial(Color.FromArgb(0x4A, 0x4F, 0x46, 0xE5));
            }
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
            if (aurora)
            {
                // 加强版: v1.1.5 原参数为 0x30
                glow = Radial(Color.FromArgb(0x55, 0x4D, 0xA3, 0xFF));
            }
        }

        backdrop.Freeze();
        glow?.Freeze();
        CurrentGlowBrush = glow;
        app.Resources["AppBackdropBrush"] = backdrop;
        // 页面根全部引用 Transparent 版 AppBackgroundBrush(App.xaml), 窗口层背景透出
        app.Resources["AppBackgroundBrush"] = TransparentBrush();
    }

    /// <summary>当前主题的极光刷子; 氛围光关闭时为 null。主窗口的极光层使用。</summary>
    public static RadialGradientBrush? CurrentGlowBrush { get; private set; }

    private static Brush TransparentBrush()
    {
        var b = new SolidColorBrush(Colors.Transparent);
        b.Freeze();
        return b;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>顶部中央的极光柔光: 中心在页面上缘、向四周渐隐。</summary>
    private static RadialGradientBrush Radial(Color centerColor)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.02),
            GradientOrigin = new Point(0.5, 0.02),
            RadiusX = 0.85,
            RadiusY = 0.65,
            GradientStops =
            {
                new GradientStop(centerColor, 0),
                new GradientStop(Color.FromArgb(0x00, centerColor.R, centerColor.G, centerColor.B), 1)
            }
        };
        brush.Freeze();
        return brush;
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
