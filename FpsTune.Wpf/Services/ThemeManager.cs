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

    // 页面底色: 顶部带蓝调的垂直渐变 + 顶部中央极光柔光(用户选定加强版参数)。
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
        app.Resources["AppBackdropBrush"] = backdrop;

        // 关键: 各页面 UserControl 用 AppBackgroundBrush 做整页背景, 若只改
        // AppBackdropBrush, 窗口层的任何氛围都会被页面平色盖住。页面底色
        // = 渐变叠极光的合成画刷, 极光才会真正显示。
        app.Resources["AppBackgroundBrush"] = ComposeBackdrop(backdrop, glow);
    }

    private static Brush ComposeBackdrop(Brush backdrop, RadialGradientBrush? glow)
    {
        if (glow is null)
            return backdrop;

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing
        {
            Brush = backdrop,
            Geometry = new RectangleGeometry(new Rect(0, 0, 1, 1))
        });
        group.Children.Add(new GeometryDrawing
        {
            Brush = glow,
            Geometry = new RectangleGeometry(new Rect(0, 0, 1, 1))
        });
        var brush = new DrawingBrush
        {
            Drawing = group,
            Viewbox = new Rect(0, 0, 1, 1),
            Viewport = new Rect(0, 0, 1, 1),
            TileMode = TileMode.None
        };
        brush.Freeze();
        return brush;
    }

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
