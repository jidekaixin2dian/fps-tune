using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

public static class ThemeManager
{
    public static string CurrentMode { get; private set; } = "dark";

    private static bool _hooked;
    private static readonly ConditionalWeakTable<SolidColorBrush, SolidColorBrush> ColorSources = new();

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
            app.Dispatcher.Invoke(() => Apply("system", animate: true));
        };
    }

    public static void SetMode(string mode)
    {
        CurrentMode = mode;
        Apply(mode, animate: true);
    }

    /// <summary>
    /// 立刻换主题、不做逐笔颜色过渡。圆形揭示动画要的是"新旧两张画面"，
    /// 逐笔过渡会和揭示叠在一起变成一团糊；普通切换仍用 <see cref="SetMode"/>。
    /// </summary>
    internal static void SetModeInstant(string mode)
    {
        CurrentMode = mode;
        Apply(mode, animate: false);
    }

    public static void Apply(string mode, bool animate = false)
    {
        Application.Current.Resources["ComboPopupAnimation"] = !UiPerformance.LowSpec && SystemParameters.ClientAreaAnimation
            ? System.Windows.Controls.Primitives.PopupAnimation.Fade : System.Windows.Controls.Primitives.PopupAnimation.None;
        animate = animate && !UiPerformance.LowSpec && SystemParameters.ClientAreaAnimation
            && Application.Current.MainWindow is { IsLoaded: true, IsVisible: true };
        void SetBrush(string key, Color target) => UpdateResourceBrush(key, target, animate);
        var resolved = mode == "system" ? ResolveSystemTheme() : mode;
        var t = resolved == "light" ? LightPalette : DarkPalette;
        Application.Current.Resources["SmallEmphasisWeight"] = resolved == "light" ? FontWeights.Medium : FontWeights.Normal;
        Application.Current.Resources["StrongEmphasisWeight"] = resolved == "light" ? FontWeights.SemiBold : FontWeights.Medium;
        Application.Current.Resources["HeavyEmphasisWeight"] = resolved == "light" ? FontWeights.Bold : FontWeights.SemiBold;
        // 只有一套视觉风格：强调色由调色板给出（工作台绿），不再按界面模式切成两套配色。
        var accent = t.Accent;
        var backdrop = t.AppBackground;

        SetBrush("ConsoleBackgroundBrush", backdrop);
        // 镀铬层（标题栏/页签栏）高通透，窗口底色从所有区域透上来；
        // 内容表面保留足够不透明度保证文字可读。
        SetBrush("SidebarBackgroundBrush", WithAlpha(t.Sidebar, resolved == "light" ? (byte)0x7A : (byte)0x52));
        SetBrush("SurfaceBrush", WithAlpha(t.Surface, resolved == "light" ? (byte)0xE8 : (byte)0xC4));
        SetBrush("SurfaceAltBrush", WithAlpha(t.SurfaceAlt, resolved == "light" ? (byte)0xEC : (byte)0xCC));
        SetBrush("ElevatedBrush", WithAlpha(t.Elevated, resolved == "light" ? (byte)0xF0 : (byte)0xD8));
        SetBrush("NotificationBackgroundBrush", t.Elevated);
        SetBrush("InputBackgroundBrush", WithAlpha(t.Input, resolved == "light" ? (byte)0xEA : (byte)0xCC));
        SetBrush("BorderBrush", t.Border);
        SetBrush("BorderHoverBrush", t.BorderHover);
        // 窗口外框专用: 比卡片边框深一档, 保证圆角描边在两种主题下都清晰可辨
        SetBrush("WindowBorderBrush", t.WindowBorder);
        SetBrush("TextPrimaryBrush", t.TextPrimary);
        SetBrush("TextSecondaryBrush", t.TextSecondary);
        SetBrush("TextMutedBrush", t.TextMuted);
        SetBrush("PrimaryBrush", t.Primary);
        SetBrush("ChartSecondaryBrush", t.Secondary);
        SetBrush("PrimaryHoverBrush", t.PrimaryHover);
        SetBrush("OnPrimaryBrush", t.OnPrimary);
        SetBrush("AccentBrush", accent);
        // 选中态软底：强调色 12–14% 透明铺底，深/浅主题下都刚好托住文字不抢层级
        SetBrush("AccentSoftBrush", WithAlpha(accent, resolved == "light" ? (byte)0x20 : (byte)0x24));
        SetBrush("DangerBrush", t.Danger);
        SetBrush("OnDangerBrush", Parse("#FFFFFF"));
        // 模态遮罩：浅色主题下用深墨色而不是纯黑，深色主题保持 60% 黑。
        SetBrush("ScrimBrush", Parse(resolved == "light" ? "#B30A0E14" : "#99000000"));
        SetBrush("WarningBrush", t.Warning);
        SetBrush("OkBrush", t.Ok);
        Application.Current.Resources["PrimaryColor"] = t.Primary;
        Application.Current.Resources["AccentColor"] = t.Accent;
        CornerPreferences.Apply();
        ApplyBackdrop(t.AppBackground, animate);
    }

    // 页面底色只提供基础对比（纯平底色铺在主窗口层），
    // 不放进页面背景——否则会随页面滑入动画平移。
    private static void ApplyBackdrop(Color backdrop, bool animate)
    {
        var app = Application.Current;
        if (app is null)
            return;

        // 纯平风格（用户决策）：去掉渐变底，窗口层与页面层用同一颜色，
        // 透明页与不透明页在任何主题下观感完全一致。
        // 一套风格：不做投影，靠平面 + 发丝分割线分层；资源键保留，低配与否都置空
        app.Resources["CardShadowEffect"] = null;

        UpdateResourceBrush("AppBackdropBrush", backdrop, animate);
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

    private static void UpdateResourceBrush(string key, Color target, bool animate)
    {
        var resources = Application.Current.Resources;
        if (resources[key] is not SolidColorBrush { IsFrozen: false } brush)
        {
            resources[key] = CreateThemeBrush(target);
            return;
        }
        TransitionBrush(brush, target, animate);
    }

    internal static SolidColorBrush CreateThemeBrush(Color initial)
    {
        // ResourceDictionary seals ordinary brushes on lookup. A live color
        // binding keeps the shared brush animatable without dummy animation clocks.
        var source = new SolidColorBrush(initial);
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
            new Binding(nameof(SolidColorBrush.Color)) { Source = source, Mode = BindingMode.OneWay });
        ColorSources.Add(brush, source);
        return brush;
    }

    internal static void TransitionBrush(SolidColorBrush brush, Color target, bool animate)
    {
        // Read the displayed color before replacing the previous animation. Rapid
        // switches continue from the current frame, replacing rather than stacking clocks.
        var from = brush.Color;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        if (ColorSources.TryGetValue(brush, out var source)) source.Color = target;
        else brush.Color = target;
        if (!animate || from == target) return;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
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
        /// <summary>图表副色：不与强调色抢层级，只在多序列里区分。</summary>
        public Color Secondary { get; }
        public Color Danger { get; }
        public Color Warning { get; }
        public Color Ok { get; }

        public Palette(
            string appBackground, string sidebar, string surface, string surfaceAlt,
            string elevated, string input, string border, string borderHover, string windowBorder,
            string textPrimary, string textSecondary, string textMuted,
            string primary, string primaryHover, string onPrimary, string accent,
            string danger, string warning, string ok, string? secondary = null)
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
            Secondary = Parse(secondary ?? accent);
            Danger = Parse(danger);
            Warning = Parse(warning);
            Ok = Parse(ok);
        }
    }

    // 唯一一套配色（工作台）：近黑底 · 纸白字 · 荧光绿动作；文字三级灰阶。
    // 旧的"控制台青/蓝"配色不再维护，副色只保留给图表用。
    private static readonly Palette DarkPalette = new(
        "#0C1015", "#0C1015", "#15191F", "#1B1F25", "#20242A", "#1B1F25",
        "#1C2026", "#23272D", "#23272D", "#E8EEF5", "#ABB8C9", "#8896A8",
        "#B8DD8D", "#CBE9A8", "#172113", "#B8DD8D", "#F87171", "#F2B75C", "#34D399",
        secondary: "#67E8F9");

    private static readonly Palette LightPalette = new(
        "#F7F9FC", "#F7F9FC", "#FFFFFF", "#F8FAFC", "#F2F5F8", "#F2F5F8",
        "#F8F9FB", "#EAF0F5", "#EAF0F5", "#14202E", "#3A4A61", "#5D6B84",
        "#426E2F", "#355826", "#FFFFFF", "#426E2F", "#DC2626", "#B45309", "#059669",
        secondary: "#0E7490");
}
