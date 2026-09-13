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
        SetBrush("ConsoleBackgroundBrush", Parse(resolved == "light" ? "#F7F9FC" : "#07090B"));
        // 控制台风格：镀铬层（标题栏/页签栏）高通透，渐变底从所有区域透上来；
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

    // 页面底色只提供基础对比（纯平底色铺在主窗口层），
    // 不放进页面背景——否则会随页面滑入动画平移。
    private static void ApplyBackdrop(string resolved)
    {
        var app = Application.Current;
        if (app is null)
            return;

        // 纯平风格（用户决策）：去掉渐变底，窗口层与页面层用同一颜色，
        // 透明页与不透明页在任何主题下观感完全一致。
        var backdrop = new SolidColorBrush(Parse(resolved == "light" ? "#F7F9FC" : "#07090B"));
        backdrop.Freeze();

        // 控制台风格无投影：平面 + 发丝分割线；保留资源键，低配与否都置空
        app.Resources["CardShadowEffect"] = null;

        app.Resources["AppBackdropBrush"] = backdrop;
        // 页面根全部引用 Transparent 版 AppBackgroundBrush(App.xaml), 窗口层背景透出
        app.Resources["AppBackgroundBrush"] = TransparentBrush();
    }

    private static Brush TransparentBrush()
    {
        var b = new SolidColorBrush(Colors.Transparent);
        b.Freeze();
        return b;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

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
