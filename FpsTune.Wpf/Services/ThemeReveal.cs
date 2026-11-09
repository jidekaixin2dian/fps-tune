using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 深浅主题切换的圆形揭示：从鼠标位置开始，把旧界面像一张图一样挖开，露出新主题。
///
/// 做法（不引依赖、不动窗口模板）：
/// 1. 切换前把主窗渲染成一张位图；
/// 2. 立刻换主题（不做逐笔颜色过渡，否则和揭示动画打架）；
/// 3. 盖一层无边框透明覆盖窗显示旧位图，把内容裁剪成「整窗减去一个圆」，圆弧半径从 0 长到盖住整窗；
/// 4. 动画结束移除覆盖窗——看到的效果就是新主题从鼠标处扩散。
///
/// 以下情况直接换主题、不播动画：低配模式、系统关掉了客户端动画、主窗还没就绪/已最小化、
/// 截图失败、覆盖窗创建失败。动画只是观感，不能让它挡住真正要做的事。
/// </summary>
internal static class ThemeReveal
{
    private const int DurationMs = 380;

    /// <summary>测试关闭动画用；默认开启。</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>测试注入：null 按环境判断（低配/系统动画开关），true/false 强制。</summary>
    internal static bool? ForceAnimate { get; set; }

    /// <summary>动画进行中的覆盖窗（测试用来断言它出现过、结束后被移除）。</summary>
    internal static Window? ActiveOverlay { get; private set; }

    /// <summary>本轮揭示的圆弧（测试可在动画中途读它的 RadiusX 确认真的在扩散）。</summary>
    internal static EllipseGeometry? ActiveHole { get; private set; }

    /// <summary>累计播放次数与最后一次的目标半径，测试与诊断用。</summary>
    internal static int Runs { get; private set; }
    internal static double LastTargetRadius { get; private set; }

    internal static void SwitchTheme(string mode)
    {
        var window = Application.Current?.MainWindow;
        var allowed = ForceAnimate ?? (Enabled && !UiPerformance.LowSpec && SystemParameters.ClientAreaAnimation);
        var animate = allowed
                      && window is { IsLoaded: true, IsVisible: true }
                      && window.WindowState != WindowState.Minimized;

        if (!animate)
        {
            ThemeManager.SetModeInstant(mode);
            return;
        }

        // 先把旧界面拍下来，再切主题：顺序反了就拍到新主题，什么都看不到。
        var snapshot = Snapshot(window!);
        ThemeManager.SetModeInstant(mode);
        if (snapshot is null)
            return;
        ShowReveal(window!, snapshot);
    }

    private static BitmapSource? Snapshot(Window window)
    {
        try
        {
            var source = PresentationSource.FromVisual(window);
            var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var width = (int)Math.Ceiling(window.ActualWidth * toDevice.M11);
            var height = (int)Math.Ceiling(window.ActualHeight * toDevice.M22);
            if (width <= 0 || height <= 0)
                return null;

            var bitmap = new RenderTargetBitmap(width, height, 96 * toDevice.M11, 96 * toDevice.M22, PixelFormats.Pbgra32);
            bitmap.Render(window);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void ShowReveal(Window owner, BitmapSource snapshot)
    {
        CloseOverlay();

        var width = owner.ActualWidth;
        var height = owner.ActualHeight;
        var center = CenterOfInterest(owner, width, height);
        var target = MaxRadius(center, width, height);

        var hole = new EllipseGeometry(center, 0, 0);
        var clip = new GeometryGroup { FillRule = FillRule.EvenOdd };
        clip.Children.Add(new RectangleGeometry(new Rect(0, 0, width, height)));
        clip.Children.Add(hole);

        var host = new Grid { Clip = clip, IsHitTestVisible = false };
        host.Children.Add(new Image { Source = snapshot, Stretch = Stretch.Fill });

        var overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,          // 换主题不抢焦点
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Owner = owner,
            Content = host
        };

        // 对齐到主窗客户区的屏幕位置（DIP）：WindowChrome 下 Left/Top 与外框有几像素差。
        try
        {
            var toDevice = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var topLeft = owner.PointToScreen(new Point(0, 0));
            overlay.Left = topLeft.X / toDevice.M11;
            overlay.Top = topLeft.Y / toDevice.M22;
        }
        catch
        {
            overlay.Left = owner.Left;
            overlay.Top = owner.Top;
        }
        overlay.Width = width;
        overlay.Height = height;

        // 半径的 X/Y 各用一条动画：冻结后的 Timeline 不能再挂事件，两条各自独立最省事。
        DoubleAnimation Grow() => new(0, target, TimeSpan.FromMilliseconds(DurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var growX = Grow();
        var growY = Grow();
        growX.Completed += (_, _) => CloseOverlay();

        try
        {
            overlay.Show();
        }
        catch
        {
            CloseOverlay();
            return;
        }

        Runs++;
        LastTargetRadius = target;
        ActiveHole = hole;
        ActiveOverlay = overlay;
        hole.BeginAnimation(EllipseGeometry.RadiusXProperty, growX);
        hole.BeginAnimation(EllipseGeometry.RadiusYProperty, growY);
    }

    /// <summary>动画圆心：鼠标在窗口内就用鼠标位置，否则退回窗口中心。</summary>
    private static Point CenterOfInterest(Window owner, double width, double height)
    {
        try
        {
            var mouse = Mouse.GetPosition(owner);
            if (mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= width && mouse.Y <= height)
                return mouse;
        }
        catch
        {
            // 鼠标查询失败按中心点处理，不影响换主题。
        }
        return new Point(width / 2, height / 2);
    }

    private static double MaxRadius(Point center, double width, double height)
    {
        static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        var farthest = Math.Max(
            Math.Max(Distance(center, new Point(0, 0)), Distance(center, new Point(width, 0))),
            Math.Max(Distance(center, new Point(0, height)), Distance(center, new Point(width, height))));
        return farthest + 8;
    }

    private static void CloseOverlay()
    {
        var overlay = ActiveOverlay;
        ActiveOverlay = null;
        ActiveHole = null;
        if (overlay is null)
            return;
        try
        {
            overlay.Close();
        }
        catch
        {
            // 关闭失败不影响主题已切换的结果。
        }
    }
}
