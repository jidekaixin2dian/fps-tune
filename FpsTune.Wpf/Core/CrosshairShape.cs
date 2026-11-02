using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 准星图形构建：覆盖窗和设置页预览共用同一套形状，
/// 避免“设置里看是一个样、游戏里画出来是另一个样”。
/// </summary>
public static class CrosshairShape
{
    public static FrameworkElement Build(CrosshairConfig config)
    {
        var root = new Grid { Width = 160, Height = 160 };
        var color = BrushFromHex(config.Color);
        double length = config.Size, thickness = config.Thickness, gap = config.Gap;
        switch (config.Style)
        {
            case "dot":
                root.Children.Add(Center(new Ellipse { Width = length, Height = length, Fill = color }));
                break;
            case "circle":
                root.Children.Add(Center(new Ellipse
                {
                    Width = length * 2, Height = length * 2,
                    Stroke = color, StrokeThickness = thickness,
                }));
                break;
            case "crossdot":
                AddCross(root, color, length, thickness, gap);
                root.Children.Add(Center(new Ellipse { Width = thickness, Height = thickness, Fill = color }));
                break;
            default:
                AddCross(root, color, length, thickness, gap);
                break;
        }
        root.Opacity = Math.Clamp(config.Opacity, 10, 100) / 100.0;
        return root;
    }

    public static Brush BrushFromHex(string hex)
    {
        try { return (Brush)(new BrushConverter().ConvertFromString(hex) ?? Brushes.Lime); }
        catch { return Brushes.Lime; }
    }

    private static void AddCross(Grid root, Brush color, double length, double thickness, double gap)
    {
        root.Children.Add(Center(new Rectangle { Width = thickness, Height = length, Fill = color, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, gap) }));
        root.Children.Add(Center(new Rectangle { Width = thickness, Height = length, Fill = color, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, gap, 0, 0) }));
        root.Children.Add(Center(new Rectangle { Width = length, Height = thickness, Fill = color, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(gap, 0, 0, 0) }));
        root.Children.Add(Center(new Rectangle { Width = length, Height = thickness, Fill = color, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, gap, 0) }));
    }

    private static FrameworkElement Center(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        element.IsHitTestVisible = false;
        return element;
    }
}
