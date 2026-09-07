using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FpsTune.Wpf.Views;

/// <summary>有界的实时图表；保存最近一屏数据，在布局尺寸变化时立即重绘。</summary>
public static class MiniChart
{
    private sealed class ChartState
    {
        public double[] Values = [];
        public string BrushKey = "AccentBrush";
        public int WindowSize = 60;
    }
    private static readonly ConditionalWeakTable<Canvas, ChartState> States = new();
    public static void Draw(Canvas canvas, IReadOnlyList<double> values, string brushKey, int windowSize = 60)
    {
        if (!States.TryGetValue(canvas, out var state))
        {
            state = new ChartState();
            States.Add(canvas, state);
            canvas.ClipToBounds = true;
            canvas.SizeChanged += (_, _) => Render(canvas, state);
        }
        state.WindowSize = Math.Max(2, windowSize);
        state.Values = values.Skip(Math.Max(0, values.Count - state.WindowSize)).ToArray();
        state.BrushKey = brushKey;
        Render(canvas, state);
    }
    private static void Render(Canvas canvas, ChartState state)
    {
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;
        Brush BrushFor(string key) => canvas.TryFindResource(key) as Brush ?? Brushes.Gray;
        foreach (var y in new[] { h - 1, h / 2 })
            canvas.Children.Add(new Line { X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = BrushFor("BorderBrush"), StrokeThickness = 1, Opacity = 0.5 });
        var stroke = BrushFor(state.BrushKey);
        Polyline? segment = null;
        Point? last = null;
        for (var i = 0; i < state.Values.Length; i++)
        {
            // 不可用的采样留空，避免把缺失数据拼成连续的趋势。
            if (!double.IsFinite(state.Values[i])) { segment = null; last = null; continue; }
            var x = 3 + (w - 6) * (state.WindowSize - state.Values.Length + i) / (state.WindowSize - 1);
            var y = h - 3 - (h - 6) * Math.Clamp(state.Values[i], 0, 100) / 100;
            if (segment is null)
            {
                segment = new Polyline { Stroke = stroke, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
                canvas.Children.Add(segment);
            }
            last = new Point(x, y);
            segment.Points.Add(last.Value);
        }
        if (last is { } point)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = stroke };
            Canvas.SetLeft(dot, point.X - 3);
            Canvas.SetTop(dot, point.Y - 3);
            canvas.Children.Add(dot);
        }
    }
}
