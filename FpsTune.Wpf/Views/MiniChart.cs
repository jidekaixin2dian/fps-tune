using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 检测页与性能会话页共用的迷你折线图：WPF 基本图元，无第三方库。
/// values 为 0-100 的百分比序列（NaN/非有限值跳过）。
/// </summary>
public static class MiniChart
{
    public static void Draw(Canvas canvas, IReadOnlyList<double> values, string brushKey, int windowSize = 60)
    {
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 10)
            return;
        canvas.Children.Clear();

        // 底线与半高线
        for (var i = 0; i < 2; i++)
        {
            var y = i == 0 ? h - 1 : h / 2;
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = (Brush)Application.Current.Resources["BorderBrush"],
                StrokeThickness = 1,
                Opacity = 0.5
            });
        }

        // 非有限值（该指标本轮不可用）跳过，避免 NaN 坐标破坏渲染
        var finite = new List<double>();
        foreach (var v in values)
            if (double.IsFinite(v))
                finite.Add(v);
        if (finite.Count == 0)
            return;

        var stroke = (Brush)Application.Current.Resources[brushKey];
        double Step() => w / Math.Max(windowSize - 1, finite.Count - 1);
        var offset = windowSize - finite.Count;
        var points = new System.Windows.Media.PointCollection();
        for (var i = 0; i < finite.Count; i++)
            points.Add(new Point(offset * Step() + i * Step(), h - 2 - (h - 4) * finite[i] / 100));
        canvas.Children.Add(new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = stroke,
            StrokeThickness = 1.6,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        });

        canvas.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6,
            Fill = stroke,
            Margin = new Thickness(points[^1].X - 3, points[^1].Y - 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
    }
}
