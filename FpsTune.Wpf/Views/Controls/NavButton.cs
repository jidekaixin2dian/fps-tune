using System.Windows;
using System.Windows.Controls;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>
/// 顶部页签导航按钮：mono 文本 + 底部指示线（控制台风格，无图标）。
/// 文本颜色跟随 Foreground，由样式在悬停/选中态切换。
/// </summary>
public class NavButton : RadioButton
{
    static NavButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(NavButton),
            new FrameworkPropertyMetadata(typeof(NavButton)));
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(NavButton),
            new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
