using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using FpsTune.Wpf.Views.Controls;
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
    [Fact]
    public void Crosshair_palette_swatches_hsv_and_hex_box_share_one_state()
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
                var swatches = (WrapPanel)view.FindName("CrosshairSwatchPanel");
                var hue = (Slider)view.FindName("CrosshairHueSlider");
                var saturation = (Slider)view.FindName("CrosshairSaturationSlider");
                var brightness = (Slider)view.FindName("CrosshairBrightnessSlider");
                var chip = (Border)view.FindName("CrosshairColorSwatch");
                Assert.NotNull(swatches); Assert.NotNull(chip);
                Assert.Equal(CrosshairColorMath.Presets.Length, swatches.Children.Count);

                // ① 十六进制框 → 调色盘与当前色块
                colorBox.Text = "#FF3B30";
                Assert.Equal("#FF3B30", SettingsService.Current.CrosshairColor);
                Assert.InRange(hue.Value, 0, 10);
                Assert.InRange(saturation.Value, 75, 90);
                Assert.Equal(100d, brightness.Value);
                var brush = Assert.IsType<SolidColorBrush>(chip.Background);
                Assert.Equal(Color.FromRgb(0xFF, 0x3B, 0x30), brush.Color);

                // ② HSV 滑杆 → 十六进制框与设置
                saturation.Value = 50;
                Assert.NotEqual("#FF3B30", colorBox.Text);
                Assert.True(CrosshairColorMath.TryParse(colorBox.Text, out var parsed), colorBox.Text);
                Assert.InRange(parsed.G, 110, 145);
                Assert.Equal(colorBox.Text, SettingsService.Current.CrosshairColor);
                Assert.Equal(Color.FromRgb(parsed.R, parsed.G, parsed.B), ((SolidColorBrush)chip.Background).Color);

                // ③ 预设色块 → 两处都是它，且只有它带强调边框
                var swatch = swatches.Children.OfType<Button>().First(b => (string?)b.Tag == "#00E5FF");
                swatch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal("#00E5FF", SettingsService.Current.CrosshairColor);
                Assert.Equal("#00E5FF", colorBox.Text);
                Assert.Equal(2d, swatch.BorderThickness.Left);
                Assert.DoesNotContain(swatches.Children.OfType<Button>().Where(b => b != swatch), b => b.BorderThickness.Left > 1);

                // ④ 预设色是精确值：滑杆只是取整回填，不能把 #00E5FF 改写成 #00D9FF 这种取整色。
                app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                System.Threading.Thread.Sleep(200);
                app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal("#00E5FF", colorBox.Text);
                Assert.Equal("#00E5FF", SettingsService.Current.CrosshairColor);
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
    public void Frame_fix_page_is_a_three_step_wizard_with_per_step_revert()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupPolishUiTests)); return; }
        RunUi((app, _) =>
        {
            var window = new MainWindow { Width = 1180, Height = 760 };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                window.NavigateTo("deltafix"); window.UpdateLayout();
                var page = (DeltaFixView)((ContentControl)window.FindName("PageHost")).Content;
                // 页面在 Loaded 时才扫描（同真实进入页面的时序），先等这一步落地再断言状态。
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)page.FindName("ScanText")).Text.Length > 0);
                foreach (var name in new[] { "StepPill1", "StepPill2", "StepPill3", "StepState1", "StepState2", "StepState3",
                             "PsoFixButton", "PsoRestoreButton", "LegacyFixButton", "LegacyRestoreButton", "RescanButton" })
                    Assert.NotNull(page.FindName(name));
                // 还原按钮的可用性跟着真实扫描结果走（有备份才能还原），不能自说自话。
                var scan = DeltaFrameFixService.Scan();
                Assert.Equal(scan.PsoBackup is not null, ((Button)page.FindName("PsoRestoreButton")).IsEnabled);
                Assert.Equal(scan.SavedBackup is not null || scan.DxBackup is not null,
                    ((Button)page.FindName("LegacyRestoreButton")).IsEnabled);
                Assert.Equal(scan.GameFound, ((Button)page.FindName("PsoFixButton")).IsEnabled);
                var stepOne = (TextBlock)page.FindName("StepState1");
                Assert.Equal(Str.T(scan.GameFound ? "Str.DeltaFixStateDone" : "Str.DeltaFixStatePending"), stepOne.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Color_preview_gives_width_to_the_image_in_window_and_fullscreen()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupPolishUiTests)); return; }
        RunUi((app, _) =>
        {
            var panel = new ScreenColorPanel();
            try
            {
                var window = new MainWindowStub(panel) { Width = 1100, Height = 820 };
                app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
                panel.UpdateLayout();
                // 参考实现版式：左列预设，右列「预览 + 实时调色」。预设列按 26% 夹在 340–420。
                var presets = (ColumnDefinition)panel.FindName("PresetColumn");
                var previewColumn = (ColumnDefinition)panel.FindName("PreviewColumn");
                var workspace = (Grid)panel.FindName("WorkspaceGrid");
                var viewbox = (FrameworkElement)FindAll<ColorPreviewControl>(panel).Single().FindName("ComparisonViewbox");

                Assert.Equal(340d, presets.Width.Value, 1);
                Assert.True(previewColumn.ActualWidth > presets.Width.Value, $"预览列没有比预设列宽：{previewColumn.ActualWidth:0}");
                Assert.True(viewbox.ActualWidth > 400, $"常规窗口下预览太小：{viewbox.ActualWidth:0}");

                // 宽屏/全屏：工作区不再卡 1640，预设列夹到上限 420，其余归预览。
                Assert.True(workspace.MaxWidth >= 2000, $"工作区最大宽度仍被卡在 {workspace.MaxWidth}");
                panel.Width = 2000; panel.UpdateLayout();
                Assert.Equal(420d, presets.Width.Value, 1);
                Assert.True(previewColumn.ActualWidth > 1200, $"宽屏下预览列仍然偏窄：{previewColumn.ActualWidth:0}");
            }
            finally { app.MainWindow.Content = null; }
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
