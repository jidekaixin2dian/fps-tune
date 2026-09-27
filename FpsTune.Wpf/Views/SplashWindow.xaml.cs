using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Reflection;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 启动画面：主窗完成构建与首帧渲染前给用户的加载反馈。
/// 在 OnStartup 里先于 MainWindow 显示，主窗 <c>ContentRendered</c> 后淡出关闭（见 App.xaml.cs）。
/// 动画用本地 Storyboard 常驻循环；低配模式跳过淡出，直接关。
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        StartSpinner();
    }

    private void StartSpinner()
    {
        // 圆环弧线匀速旋转：经典 spinner，比呼吸圆点更清晰
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    /// <summary>主窗就绪后调用：短淡出再关闭，避免生硬跳变。</summary>
    public void CloseWithFade()
    {
        if (UiPerformance.LowSpec)
        {
            Close();
            return;
        }

        IsHitTestVisible = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
