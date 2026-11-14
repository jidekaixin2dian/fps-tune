using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// HUD 帧时间折线的真渲染：卡片布局露出折线与「拍下现场」、卡顿点标红、紧凑布局隐藏，
/// 深/浅主题截图存 work/readability-frames/ui/hud-timeline-*.png。
/// 数据走 HUD 自己的 Timeline 缓冲（Append → Refresh → Analyze → Render 全路径）。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class HudTimelineUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private const double SixtyFps = 1000.0 / 60;

    private static void FeedTimeline(PerformanceHudWindow hud, int stutterIndex = 59)
    {
        for (var i = 0; i < 60; i++)
            hud.Timeline.Append(i == stutterIndex ? SixtyFps * 4 : SixtyFps);
        hud.Refresh();
    }

    [Fact]
    public void Hud_timeline_renders_line_and_stutter_dots_in_both_themes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(HudTimelineUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var session = new PerformanceSessionService(
                (interval, capacity) => new MetricsSampler(interval, capacity),
                () => new SessionFpsRecorder(findPresentMon: () => null));
            using var metrics = new MetricsSampler(TimeSpan.FromSeconds(1), 60,
                () => new MetricSample(DateTime.Now, 30, 50, 70, 2UL << 30, 8UL << 30));
            var hud = new PerformanceHudWindow(session, metrics);
            DlssFeedbackTests.PrepareBackgroundWindow(hud);
            try
            {
                hud.Show();
                hud.ApplyAppearance(new HudAppearance("card", 0, "top-right"));
                DlssFeedbackTests.PumpUntil(() => ((Canvas)hud.FindName("TimelineCanvas")!).ActualWidth > 20);
                var area = (Grid)hud.FindName("TimelineArea")!;
                var button = (Button)hud.FindName("EvidenceButton")!;
                Assert.Equal(Visibility.Visible, area.Visibility);
                Assert.Equal(Visibility.Visible, button.Visibility);

                FeedTimeline(hud);
                var canvas = (Canvas)hud.FindName("TimelineCanvas")!;
                var areaVis = ((Grid)hud.FindName("TimelineArea")!).Visibility;
                Assert.True(canvas.Children.OfType<Polyline>().Any(),
                    $"polyline={canvas.Children.OfType<Polyline>().Count()}, children={canvas.Children.Count}, area={areaVis}, w={canvas.ActualWidth:0.0}, h={canvas.ActualHeight:0.0}, frames={(hud.Timeline.Analyze()?.Frames.Count ?? -1)}");
                var dots = canvas.Children.OfType<Ellipse>().Count();
                var snap = hud.Timeline.Analyze();
                Assert.True(dots == 1, $"dots={dots}, children={canvas.Children.Count}, stutters=[{string.Join(",", snap?.StutterIndices ?? [])}], frames={snap?.Frames.Count}"); // 1 个卡顿点标红
                var stutterText = (TextBlock)hud.FindName("StutterText")!;
                Assert.Contains("1", stutterText.Text);

                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); hud.UpdateLayout(); hud.Refresh();
                    ReadabilityAndFramesUiTests.Render(hud, "hud-timeline-card-" + theme);
                }

                // 紧凑布局不露折线与按钮。
                hud.ApplyAppearance(new HudAppearance("compact", 0, "top-right"));
                Assert.Equal(Visibility.Collapsed, area.Visibility);
                Assert.Equal(Visibility.Collapsed, button.Visibility);
                ThemeManager.SetMode("light"); hud.UpdateLayout();
                ReadabilityAndFramesUiTests.Render(hud, "hud-timeline-compact-light");
            }
            finally
            {
                hud.Close();
                session.Dispose();
            }
        });
    }

    [Fact]
    public void Hud_timeline_clears_when_buffer_empties()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(HudTimelineUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var session = new PerformanceSessionService(
                (interval, capacity) => new MetricsSampler(interval, capacity),
                () => new SessionFpsRecorder(findPresentMon: () => null));
            using var metrics = new MetricsSampler(TimeSpan.FromSeconds(1), 60,
                () => new MetricSample(DateTime.Now, 30, 50, 70, 2UL << 30, 8UL << 30));
            var hud = new PerformanceHudWindow(session, metrics);
            DlssFeedbackTests.PrepareBackgroundWindow(hud);
            try
            {
                hud.Show();
                hud.ApplyAppearance(new HudAppearance("card", 0, "top-right"));
                DlssFeedbackTests.PumpUntil(() => ((Canvas)hud.FindName("TimelineCanvas")!).ActualWidth > 20);
                FeedTimeline(hud);
                var canvas = (Canvas)hud.FindName("TimelineCanvas")!;
                Assert.True(canvas.Children.Count > 0);
                hud.Timeline.Clear();
                hud.Refresh();
                Assert.Empty(canvas.Children);
                Assert.Equal("", ((TextBlock)hud.FindName("StutterText")!).Text);
            }
            finally
            {
                hud.Close();
                session.Dispose();
            }
        });
    }
}
