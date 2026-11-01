using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>
/// 滚轮穿透：鼠标停在滑块或下拉框上时，WPF 默认把滚轮交给这些控件去改数值/换选项，
/// 页面就滚不动——用户报的"滚轮没修好"多数是踩到这里。这里把落在它们身上的滚轮
/// 改成滚动所在的页面；列表、文本这类本来就是滚动的区域保持原生行为。
/// </summary>
internal static class WheelPassthrough
{
    /// <summary>挂到窗口或页面根元素上，只装一次。</summary>
    internal static void Attach(FrameworkElement root) => root.PreviewMouseWheel += OnPreviewMouseWheel;

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not DependencyObject source) return;
        if (!IsValueControl(source)) return;
        if (FindAncestor<ScrollViewer>(source) is not { } viewer) return;
        if (viewer.ScrollableHeight <= 0) return;
        var target = Math.Clamp(viewer.VerticalOffset - e.Delta, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < 0.5) return;
        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    /// <summary>只有"吃滚轮改值"的控件需要改写；其余保持原样。</summary>
    private static bool IsValueControl(DependencyObject source)
        => FindAncestor<Slider>(source) is not null || FindAncestor<ComboBox>(source) is not null;

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
