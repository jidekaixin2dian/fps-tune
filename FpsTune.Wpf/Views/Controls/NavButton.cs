using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

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
            new PropertyMetadata(string.Empty, (d, e) =>
                AutomationProperties.SetName((NavButton)d, (string)e.NewValue ?? string.Empty)));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>可选线性图标（SVG path data，16×16 视窗，stroke 渲染）；空 = 不显示。</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(NavButton),
            new PropertyMetadata(string.Empty));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

/// <summary>侧栏动作项（非页面导航，如"掉帧修复"）：外观同子项，支持同款线性图标。</summary>
public class NavActionButton : Button
{
    static NavActionButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(NavActionButton),
            new FrameworkPropertyMetadata(typeof(NavActionButton)));
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(NavActionButton),
            new PropertyMetadata(string.Empty));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>动作页正在显示时的激活态：外观与页签选中一致（强调色 + 指示条）。</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(NavActionButton),
            new PropertyMetadata(false));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }
}
