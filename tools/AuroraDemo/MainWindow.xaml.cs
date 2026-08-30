using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AuroraDemo;

public partial class MainWindow : Window
{
    // 极光参数与主窗口 v1.1.5 原实现一致：顶部中央径向柔光，中心到边缘渐隐。
    // 颜色 = 主题主色/强调色族，透明度即"强度"。
    private static readonly (byte alpha, Color color) DarkGlowNormal = (0x30, Color.FromRgb(0x4D, 0xA3, 0xFF));
    private static readonly (byte alpha, Color color) DarkGlowStrong = (0x55, Color.FromRgb(0x4D, 0xA3, 0xFF));
    private static readonly (byte alpha, Color color) LightGlowNormal = (0x22, Color.FromRgb(0x4F, 0x46, 0xE5));
    private static readonly (byte alpha, Color color) LightGlowStrong = (0x4A, Color.FromRgb(0x4F, 0x46, 0xE5));

    public MainWindow()
    {
        InitializeComponent();
        Refresh(default!, default!);
    }

    private void Refresh(object sender, RoutedEventArgs e)
    {
        if (Aurora is null || Stage is null)
            return; // XAML 解析过程中 IsChecked 触发时控件尚未就绪
        var dark = ThemeDark?.IsChecked == true;
        var mode = AuroraOff?.IsChecked == true ? 0 : AuroraNormal?.IsChecked == true ? 1 : 2;

        // 底色渐变（与 ThemeManager.ApplyBackdrop 一致）
        var backdrop = dark
            ? Linear("#121A29", "#0A0D12", 0.6)
            : Linear("#D9E6FB", "#E9EEF7", 0.55);
        Stage.Background = backdrop;

        // 极光层
        var glow = (dark, mode) switch
        {
            (_, 0) => null,
            (true, 1) => Radial(DarkGlowNormal.alpha, DarkGlowNormal.color),
            (true, _) => Radial(DarkGlowStrong.alpha, DarkGlowStrong.color),
            (false, 1) => Radial(LightGlowNormal.alpha, LightGlowNormal.color),
            (false, _) => Radial(LightGlowStrong.alpha, LightGlowStrong.color),
        };
        Aurora.Fill = glow;
        Aurora.Visibility = glow is null ? Visibility.Collapsed : Visibility.Visible;

        // 配套的主题文字/卡片颜色
        TitleBar.Background = Solid(dark ? "#0D1016" : "#FBFCFE");
        TitleBarText.Foreground = Solid(dark ? "#EDF1F7" : "#1A2233");
        BrandText.Foreground = Solid(dark ? "#EDF1F7" : "#1A2233");
        HeroTitle.Foreground = CardTitle1.Foreground = CardTitle2.Foreground = Solid(dark ? "#EDF1F7" : "#1A2233");
        HeroSub.Foreground = Solid(dark ? "#A6B1C2" : "#3E4A61");
        HeroCard.Background = Card1.Background = Card2.Background = Solid(dark ? "#12161E" : "#FFFFFF");
        var cardBorder = Solid(dark ? "#232B38" : "#E3E8F0");
        HeroCard.BorderBrush = Card1.BorderBrush = Card2.BorderBrush = cardBorder;
        CardDesc1.Foreground = CardDesc2.Foreground = Solid(dark ? "#7E8BA0" : "#5D6B85");
        HintText.Text = mode switch
        {
            0 => "极光层已关闭（当前主程序的状态）",
            1 => "原版 = v1.1.5 曾发布的参数（深 0x30 / 浅 0x22）",
            _ => "加强版（深 0x55 / 浅 0x4A），如果原版太淡可以选这个"
        };
    }

    private static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static Brush Linear(string top, string bottom, double midOffset) => new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        {
            new GradientStop((Color)ColorConverter.ConvertFromString(top), 0),
            new GradientStop((Color)ColorConverter.ConvertFromString(bottom), midOffset),
            new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 1)
        }
    };

    private static Brush Radial(byte alpha, Color color) => new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0),
            new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1)
        }
    };
}
