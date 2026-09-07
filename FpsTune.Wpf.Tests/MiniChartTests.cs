using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class MiniChartTests
{
    [Fact]
    public Task Long_history_keeps_only_recent_window_and_all_geometry_inside_canvas() => OnSta(() =>
    {
        var canvas = SizedCanvas(300);
        MiniChart.Draw(canvas, Enumerable.Range(0, 120).Select(i => (double)i).ToArray(), "AccentBrush");
        var points = Assert.Single(canvas.Children.OfType<Polyline>()).Points;
        Assert.Equal(60, points.Count);
        Assert.Equal(3, points[0].X);
        Assert.Equal(297, points[^1].X);
        Assert.All(points, p => { Assert.InRange(p.X, 3, 297); Assert.InRange(p.Y, 3, 69); });
        Assert.True(canvas.ClipToBounds);
    });

    [Fact]
    public Task Resize_redraws_existing_samples_without_waiting_for_another_sample() => OnSta(() =>
    {
        var canvas = SizedCanvas(600);
        MiniChart.Draw(canvas, new[] { 10d, 20d, 30d }, "AccentBrush");
        Resize(canvas, 240);
        var points = Assert.Single(canvas.Children.OfType<Polyline>()).Points;
        Assert.Equal(237, points[^1].X);
        Assert.All(canvas.Children.OfType<Line>(), line => Assert.Equal(240, line.X2));
        Resize(canvas, 800);
        Assert.Equal(797, Assert.Single(canvas.Children.OfType<Polyline>()).Points[^1].X);
    });

    [Fact]
    public Task Missing_values_leave_gaps_in_time_and_invalid_window_size_is_safe() => OnSta(() =>
    {
        var canvas = SizedCanvas(300);
        MiniChart.Draw(canvas, new[] { 10d, double.NaN, 30d }, "AccentBrush", 3);
        var segments = canvas.Children.OfType<Polyline>().ToArray();
        Assert.Equal(2, segments.Length);
        Assert.Equal(3, Assert.Single(segments[0].Points).X);
        Assert.Equal(297, Assert.Single(segments[1].Points).X);
        MiniChart.Draw(canvas, new[] { double.PositiveInfinity }, "AccentBrush", 0);
        Assert.Empty(canvas.Children.OfType<Polyline>());
        Assert.Empty(canvas.Children.OfType<Ellipse>());
    });

    private static Canvas SizedCanvas(double width)
    {
        var canvas = new Canvas { Height = 72 };
        Resize(canvas, width);
        return canvas;
    }
    private static void Resize(Canvas canvas, double width)
    {
        canvas.Width = width;
        canvas.Measure(new Size(width, 72));
        canvas.Arrange(new Rect(0, 0, width, 72));
        canvas.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static Task OnSta(Action test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { test(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
