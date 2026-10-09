using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public class ThemeTransitionTests
{
    [Fact]
    public void Shared_theme_brush_stays_live_when_resources_are_sealed() => Sta(() =>
    {
        var brush = ThemeManager.CreateThemeBrush(Colors.Black);
        Assert.False(brush.CanFreeze);
        ThemeManager.TransitionBrush(brush, Colors.White, false);
        Assert.Equal(Colors.White, brush.Color);
        Assert.False(brush.CanFreeze);
    });

    [Fact]
    public void Reduced_motion_cancels_an_active_transition_and_sets_the_exact_target() => Sta(() =>
    {
        var brush = new SolidColorBrush(Colors.Black);
        ThemeManager.TransitionBrush(brush, Colors.White, true);
        ThemeManager.TransitionBrush(brush, Colors.Green, false);
        Assert.False(brush.HasAnimatedProperties);
        Assert.Equal(Colors.Green, brush.Color);
    });

    [Fact]
    public void Rapid_switches_finish_at_the_latest_target_without_replacing_the_brush() => Sta(() =>
    {
        var brush = new SolidColorBrush(Colors.Black);
        ThemeManager.TransitionBrush(brush, Colors.White, true);
        Pump(60);
        Assert.True(brush.HasAnimatedProperties);
        ThemeManager.TransitionBrush(brush, Colors.Green, true);
        Assert.Equal(Colors.Green, brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty));
        Pump(300);
        Assert.Equal(Colors.Green, brush.Color);
    });

    [Fact]
    public void Unchanged_theme_starts_no_animation() => Sta(() =>
    {
        var brush = new SolidColorBrush(Colors.Black);
        ThemeManager.TransitionBrush(brush, Colors.Black, true);
        Assert.False(brush.HasAnimatedProperties);
    });

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Theme transition did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
