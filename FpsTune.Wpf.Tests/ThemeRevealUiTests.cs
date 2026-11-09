using System.Windows;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 深浅主题切换的圆形揭示：以鼠标（测试环境在窗口外时为窗口中心）为圆心，
/// 用一张旧界面快照盖住窗口，圆弧从 0 长到盖住整窗后移除覆盖层。
///
/// 守住三件事：真的播了（Runs +1）、圆心取对、结束后覆盖层被移除且主题已切。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class ThemeRevealUiTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Theme_switch_reveals_from_the_pointer_and_cleans_up_the_overlay()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ThemeRevealUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var window = new MainWindow { Width = 1180, Height = 800 };
            app.MainWindow = window;
            ThemeManager.SetMode("dark");
            DlssFeedbackTests.PrepareBackgroundWindow(window);
            window.Show();
            try
            {
                window.NavigateTo("opt");
                DlssFeedbackTests.PumpUntil(() => window.IsLoaded && window.ActualWidth > 0);

                var before = ThemeReveal.Runs;
                ThemeReveal.ForceAnimate = true;
                ThemeReveal.SwitchTheme("light");

                Assert.Equal(before + 1, ThemeReveal.Runs);
                Assert.NotNull(ThemeReveal.ActiveOverlay);
                var hole = ThemeReveal.ActiveHole;
                Assert.NotNull(hole);

                // 半径要能盖住整窗（对角线量级），圆心在窗口内且落在中心（测试机鼠标不在窗口上）。
                Assert.True(ThemeReveal.LastTargetRadius > window.ActualWidth / 2,
                    $"目标半径 {ThemeReveal.LastTargetRadius:0} 应大于半宽 {window.ActualWidth / 2:0}");
                Assert.InRange(hole!.Center.X, window.ActualWidth / 2 - 2, window.ActualWidth / 2 + 2);
                Assert.InRange(hole.Center.Y, window.ActualHeight / 2 - 2, window.ActualHeight / 2 + 2);
                Assert.True(hole.RadiusX >= 0);

                // 动画结束后覆盖层被移除，主题已经是浅色。
                DlssFeedbackTests.PumpUntil(() => ThemeReveal.ActiveOverlay is null);
                Assert.Null(ThemeReveal.ActiveHole);
                Assert.Equal("light", ThemeManager.CurrentMode);
                Assert.NotEqual(
                    ((System.Windows.Media.SolidColorBrush)app.FindResource("AppBackdropBrush")).Color,
                    System.Windows.Media.Colors.Transparent);
            }
            finally
            {
                ThemeReveal.ForceAnimate = null;
                ThemeManager.SetMode("dark");
                window.Close();
            }
        });
    }
}
