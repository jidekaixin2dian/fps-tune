using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 本轮三处界面修正的契约：
/// 一键优化方案收敛成一个下拉、外置准星颜色改完立刻生效且有同款预览、网络探测三个控件等高对齐。
/// </summary>
[Collection("BackupService serial")]
public sealed class StartupPolishUiTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Quick_optimize_uses_one_plan_dropdown_instead_of_a_row_of_chips()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupPolishUiTests)); return; }
        RunUi((app, _) =>
        {
            var view = new QuickOptimizeView();
            try
            {
                app.MainWindow = new MainWindowStub(view); DlssFeedbackTests.PrepareBackgroundWindow(app.MainWindow); app.MainWindow.Show();
                var box = (ComboBox)view.FindName("PlanBox");
                Assert.NotNull(box);
                Assert.True(box.Items.Count >= 3, $"plans={box.Items.Count}");
                Assert.Equal(0, box.SelectedIndex);
                Assert.Equal("balanced", (string)typeof(QuickOptimizeView)
                    .GetProperty("SelectedPreset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(view)!);
                Assert.Empty(FindAll<RadioButton>(view).Where(t => t.GroupName == "QuickPlan"));
            }
            finally { app.MainWindow.Content = null; }
        });
    }

    [Fact]
    public void Crosshair_color_applies_while_typing_and_preview_matches_the_config()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupPolishUiTests)); return; }
        RunUi((app, _) =>
        {
            var before = SettingsService.Current.CrosshairColor;
            var view = new CrosshairView();
            try
            {
                app.MainWindow = new MainWindowStub(view); DlssFeedbackTests.PrepareBackgroundWindow(app.MainWindow); app.MainWindow.Show();
                var colorBox = (TextBox)view.FindName("CrosshairColorBox");
                var host = (ContentControl)view.FindName("CrosshairPreviewHost");
                Assert.NotNull(colorBox);
                Assert.NotNull(host);
                Assert.NotNull(host.Content);
                colorBox.Text = "#FF3B30";
                var slider = (Slider)view.FindName("CrosshairSizeSlider");
                slider.Value = slider.Value == 14 ? 16 : 14;
                Assert.Equal("#FF3B30", SettingsService.Current.CrosshairColor);
                var shape = Assert.IsType<Grid>(host.Content);
                var filled = FindAll<System.Windows.Shapes.Shape>(shape).Select(s => (s.Fill ?? s.Stroke) as SolidColorBrush)
                    .Where(b => b is not null).Select(b => b!.Color).Distinct().ToArray();
                Assert.Contains(filled, c => c.R == 0xFF && c.G == 0x3B && c.B == 0x30);
            }
            finally
            {
                SettingsService.Current.CrosshairColor = before;
                SettingsService.Save(SettingsService.Current);
                app.MainWindow.Content = null;
            }
        });
    }

    [Fact]
    public void Network_mode_port_and_sample_controls_share_one_row_height()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupPolishUiTests)); return; }
        RunUi((app, _) =>
        {
            var view = new NetworkToolsPanel();
            try
            {
                app.MainWindow = new MainWindowStub(view); DlssFeedbackTests.PrepareBackgroundWindow(app.MainWindow); app.MainWindow.Show();
                var mode = (ComboBox)view.FindName("ModeBox");
                var port = (TextBox)view.FindName("PortBox");
                var count = (ComboBox)view.FindName("CountBox");
                Assert.Equal(30, mode.ActualHeight, 1);
                Assert.Equal(mode.ActualHeight, port.ActualHeight, 1);
                Assert.Equal(mode.ActualHeight, count.ActualHeight, 1);
                var top = mode.TranslatePoint(new Point(0, 0), view).Y;
                Assert.Equal(top, port.TranslatePoint(new Point(0, 0), view).Y, 1);
                Assert.Equal(top, count.TranslatePoint(new Point(0, 0), view).Y, 1);
            }
            finally { app.MainWindow.Content = null; }
        });
    }

    [Fact]
    public void Wallpaper_opacity_slider_keeps_its_track_width()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupPolishUiTests)); return; }
        RunUi((app, _) =>
        {
            var view = new SettingsView();
            try
            {
                app.MainWindow = new MainWindowStub(view); DlssFeedbackTests.PrepareBackgroundWindow(app.MainWindow); app.MainWindow.Show();
                var slider = (Slider)view.FindName("WallpaperOpacitySlider");
                Assert.NotNull(slider);
                // 模板只有 MaxWidth + Left 时轨道会被挤成拇指大小（用户看到“只剩一个圆点”），
                // 所以样式必须给出实宽；这条断言直接盯住那段样式，不依赖卡片是否在当前分段里显示。
                var style = slider.Style;
                Assert.NotNull(style);
                var width = style!.Setters.OfType<Setter>().FirstOrDefault(s => ReferenceEquals(s.Property, FrameworkElement.WidthProperty))?.Value;
                Assert.Equal(260d, Assert.IsType<double>(width));
                Assert.DoesNotContain(style.Setters.OfType<Setter>(), s => ReferenceEquals(s.Property, FrameworkElement.MaxWidthProperty));
            }
            finally { app.MainWindow.Content = null; }
        });
    }

    [Fact]
    public void Frame_fix_is_a_page_route_not_a_dialog()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupPolishUiTests)); return; }
        RunUi((app, _) =>
        {
            var window = new MainWindow { Width = 1180, Height = 720 };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                window.NavigateTo("deltafix"); window.UpdateLayout();
                var page = ((ContentControl)window.FindName("PageHost")).Content;
                Assert.IsType<DeltaFixView>(page);
                Assert.NotNull(((FrameworkElement)page).FindName("PsoFixButton"));
            }
            finally { window.Close(); }
        });
    }
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static void RunUi(Action<Application, string> body)
        => ReadabilityAndFramesUiTests.RunUi(body);

    private sealed class MainWindowStub : Window
    {
        public MainWindowStub(FrameworkElement content)
        {
            Content = content;
            Width = 1200;
            Height = 820;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -4000;
            Top = -4000;
        }
    }
}
