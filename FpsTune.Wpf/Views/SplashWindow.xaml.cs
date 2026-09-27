using System.Windows;
using System.Windows.Media.Animation;
using System.Reflection;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 启动画面：主窗完成构建与首帧渲染前给用户的加载反馈。
/// 在 OnStartup 里先于 MainWindow 显示，主窗 <c>ContentRendered</c> 后淡出关闭（见 App.xaml.cs）。
/// 0.1.12：进度条由 OnStartup 的真实步骤驱动（<see cref="SetPhase"/>），不是装饰性动画；
/// 低配模式跳过淡入淡出，直接显示/关闭。
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        if (!UiPerformance.LowSpec)
        {
            Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
            BeginAnimation(OpacityProperty, fadeIn);
        }
    }

    /// <summary>启动阶段推进：percent 0-100，text 为刚完成的/正在进行的一步。</summary>
    public void SetPhase(int percent, string text)
    {
        PhaseBar.Value = Math.Clamp(percent, 0, 100);
        LoadingText.Text = text;
    }

    /// <summary>主窗就绪后调用：推满进度条，短淡出再关闭，避免生硬跳变。</summary>
    public void CloseWithFade()
    {
        PhaseBar.Value = 100;
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
