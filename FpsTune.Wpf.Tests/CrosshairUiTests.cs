using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 准星页的离屏渲染与交互守卫：三种显示方式在深浅主题下各渲染一张 PNG，
/// 同时锁住「改选项确实写进设置」「按键捕获能落地」「可见文字不小于 13 号」。
/// 另加一条真机检查：覆盖窗在真实窗口 + 真实轮询下按推定状态显示/隐藏。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class CrosshairUiTests
{
    [Fact]
    public void Crosshair_page_renders_three_display_modes_and_wires_every_control()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(CrosshairUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var settings = SettingsService.Current;
            // 关着总开关再渲染：测试进程里不该真的建一个全屏覆盖窗（真机检查另有用例）。
            settings.CrosshairEnabled = false;
            settings.CrosshairDisplayMode = "always";
            settings.CrosshairAdsMode = "hold";
            settings.CrosshairAdaptiveHide = true;
            settings.CrosshairHideOnAds = true;
            settings.CrosshairFireTrigger = "MouseLeft";
            settings.CrosshairAdsTrigger = "MouseRight";
            settings.CrosshairSyncTrigger = "";
            settings.CrosshairShowDelayMs = 0;
            settings.CrosshairHideDelayMs = 0;

            // 窗口只用来跑交互与状态行：窗口高度受屏幕工作区限制，拍整页另走离屏排版。
            var window = new MainWindow { Width = 1180, Height = 900 };
            window.NavigateTo("crosshair");
            app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window);
            window.Show();
            window.UpdateLayout();
            var view = (CrosshairView)((ContentControl)window.FindName("PageHost")).Content;
            DlssFeedbackTests.PumpUntil(() => view.IsLoaded);

            var rendered = new List<string>();
            try
            {
                Assert.False(string.IsNullOrEmpty(((TextBlock)view.FindName("CrosshairModeDescText")).Text));
                // 总开关关着 + 覆盖窗没跑：运行状态要如实说「现在不会显示」。
                DlssFeedbackTests.PumpUntil(() =>
                    ((TextBlock)view.FindName("CrosshairStatusHintText")).Text == Str.T("Str.CrosshairStatusMasterOff"));
                Assert.Equal(Str.T("Str.CrosshairStatusNotPressed"), ((TextBlock)view.FindName("CrosshairFireStateText")).Text);
                Assert.Equal(Str.T("Str.CrosshairStatusAdsOff"), ((TextBlock)view.FindName("CrosshairAdsStateText")).Text);

                // 显示方式：三个选项都把选择写进设置。
                foreach (var mode in new[] { "always", "fire", "ads" })
                {
                    SelectMode(view, mode);
                    window.UpdateLayout();
                    Assert.Equal(mode, settings.CrosshairDisplayMode);
                    Assert.False(string.IsNullOrEmpty(((TextBlock)view.FindName("CrosshairModeDescText")).Text));
                }

                // 开镜方式：点一下切换写进设置。
                var toggle = (RadioButton)view.FindName("CrosshairAdsToggle");
                toggle.IsChecked = true;
                window.UpdateLayout();
                Assert.Equal("toggle", settings.CrosshairAdsMode);
                Assert.False(string.IsNullOrEmpty(((TextBlock)view.FindName("CrosshairAdsModeDescText")).Text));

                // 开镜时隐藏：老字段跟着一起写，升级后行为才不会变。
                var hideOnAds = (CheckBox)view.FindName("CrosshairHideOnAdsCheck");
                hideOnAds.IsChecked = false;
                window.UpdateLayout();
                Assert.False(settings.CrosshairHideOnAds);
                Assert.False(settings.CrosshairAdaptiveHide);

                // 延迟：滑杆写设置，读数是「立即 / 毫秒」。
                var showDelay = (Slider)view.FindName("CrosshairShowDelaySlider");
                showDelay.Value = 150;
                window.UpdateLayout();
                Assert.Equal(150, settings.CrosshairShowDelayMs);
                Assert.Equal("150 ms", ((TextBlock)view.FindName("CrosshairShowDelayValue")).Text);
                var hideDelay = (Slider)view.FindName("CrosshairHideDelaySlider");
                hideDelay.Value = 0;
                window.UpdateLayout();
                Assert.Equal(Str.T("Str.CrosshairDelayImmediate"), ((TextBlock)view.FindName("CrosshairHideDelayValue")).Text);

                // 按键捕获：点按钮 → 按下 F8 → 设置与按钮文案都换成 F8。
                var pressed = new HashSet<string>(StringComparer.Ordinal);
                var fireButton = (Button)view.FindName("CrosshairFireTriggerButton");
                Assert.Equal("MouseLeft", fireButton.Content as string);
                CrosshairTriggers.KeyStateOverride = name => pressed.Contains(name);
                try
                {
                    fireButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    DlssFeedbackTests.PumpUntil(() => fireButton.Content as string == Str.T("Str.CrosshairTriggerCapturing"));
                    PumpMs(150);
                    pressed.Add("F8");
                    DlssFeedbackTests.PumpUntil(() => settings.CrosshairFireTrigger == "F8");
                    Assert.Equal("F8", fireButton.Content as string);
                }
                finally
                {
                    pressed.Clear();
                    CrosshairTriggers.KeyStateOverride = null;
                }
                DlssFeedbackTests.PumpUntil(() => fireButton.Content as string == "F8");
                Assert.NotEqual(Str.T("Str.CrosshairTriggerCapturing"), fireButton.Content as string);
            }
            finally { window.Close(); }

            // ── 离屏整页渲染：每种显示方式在深浅主题下各一张 ──
            var fontFamily = (FontFamily)app.FindResource("FontBody");
            var backdrop = (Brush)app.FindResource("AppBackgroundBrush");
            foreach (var theme in new[] { "dark", "light" })
            {
                ThemeManager.SetMode(theme);
                DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)Application.Current.FindResource("AppBackdropBrush")).Color
                    == (Color)ColorConverter.ConvertFromString(theme == "light" ? "#F7F9FC" : "#0C1015")!);
                foreach (var mode in new[] { "always", "fire", "ads" })
                {
                    // 每张图都新造一页：状态从设置里读，和用户重开页面看到的一致。
                    settings.CrosshairDisplayMode = mode;
                    var page = new CrosshairView();
                    // FontSize 14 与主窗口 WindowTypographyStyle 一致，离屏渲染才和真机字号相同。
                    page.FontSize = 14;
                    page.FontFamily = fontFamily;
                    var host = new Border { Child = page, Background = backdrop };
                    rendered.Add(RenderFullPage(host, page, 1100, $"crosshair-{mode}-{theme}"));
                }
            }

            File.WriteAllText(Path.Combine(ShotsDir(), "rendered.json"),
                JsonSerializer.Serialize(new { frames = rendered, minFontDip = 13, themeCount = 2, modeCount = 3 },
                    new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    [Fact]
    public void Crosshair_overlay_shows_and_hides_from_key_state_on_this_machine()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(CrosshairUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var pressed = new HashSet<string>(StringComparer.Ordinal);
            CrosshairTriggers.KeyStateOverride = name => pressed.Contains(name);
            CrosshairOverlayWindow.SelfForegroundOverride = () => false;
            var observed = new List<object>();
            var overlay = new CrosshairOverlayWindow();
            Assert.True(overlay.Topmost);
            try
            {
                overlay.ApplyConfig(new CrosshairConfig(true, "cross", "#00FF66", 12, 4, 2, 90, false,
                    "fire", "hold", true, "MouseLeft", "MouseRight", "", 0, 0));
                overlay.Show();
                overlay.StartPolling();
                DlssFeedbackTests.PumpUntil(() => overlay.IsLoaded);
                DlssFeedbackTests.PumpUntil(() => overlay.Visibility == Visibility.Hidden);
                observed.Add(new { step = "idle", visible = overlay.LastFrame.Visible, phase = overlay.LastFrame.Phase.ToString() });

                pressed.Add("MouseLeft");
                DlssFeedbackTests.PumpUntil(() => overlay.Visibility == Visibility.Visible);
                observed.Add(new { step = "fire-held", visible = overlay.LastFrame.Visible, phase = overlay.LastFrame.Phase.ToString() });

                pressed.Remove("MouseLeft");
                DlssFeedbackTests.PumpUntil(() => overlay.Visibility == Visibility.Hidden);
                observed.Add(new { step = "fire-released", visible = overlay.LastFrame.Visible, phase = overlay.LastFrame.Phase.ToString() });

                // 切到战术据枪：开镜键按住就显示，松开就藏。
                overlay.ApplyConfig(new CrosshairConfig(true, "cross", "#00FF66", 12, 4, 2, 90, false,
                    "ads", "hold", true, "MouseLeft", "MouseRight", "", 0, 0));
                pressed.Add("MouseRight");
                DlssFeedbackTests.PumpUntil(() => overlay.Visibility == Visibility.Visible);
                observed.Add(new { step = "ads-held", visible = overlay.LastFrame.Visible, phase = overlay.LastFrame.Phase.ToString() });
                pressed.Remove("MouseRight");
                DlssFeedbackTests.PumpUntil(() => overlay.Visibility == Visibility.Hidden);
                observed.Add(new { step = "ads-released", visible = overlay.LastFrame.Visible, phase = overlay.LastFrame.Phase.ToString() });

                // 总开关关掉后不再显示。
                overlay.ApplyConfig(new CrosshairConfig(false, "cross", "#00FF66", 12, 4, 2, 90, false,
                    "always", "hold", true, "MouseLeft", "MouseRight", "", 0, 0));
                pressed.Add("MouseLeft");
                PumpMs(150);
                Assert.Equal(Visibility.Hidden, overlay.Visibility);
                observed.Add(new { step = "master-off", visible = overlay.LastFrame.Visible, phase = overlay.LastFrame.Phase.ToString() });

                File.WriteAllText(Path.Combine(ShotsDir(), "overlay-machine-check.json"),
                    JsonSerializer.Serialize(new { window = "CrosshairOverlayWindow", steps = observed },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                pressed.Clear();
                CrosshairTriggers.KeyStateOverride = null;
                CrosshairOverlayWindow.SelfForegroundOverride = null;
                overlay.StopPolling();
                overlay.Close();
            }

            // 真实读取路径（不注入）也不能抛：覆盖窗在真机上走的就是这一条。
            Assert.False(CrosshairTriggers.IsHeld("MouseRight") && !CrosshairTriggers.IsHeld("MouseRight"));
            _ = CrosshairTriggers.IsHeld("XButton2");
        });
    }

    /// <summary>
    /// 把整页排开再渲染：窗口受屏幕工作区高度限制，拍不下整页，
    /// 所以离屏按「标题区 + 列表内容」的实际高度测量、排布，再整张画出来。
    /// </summary>
    private static string RenderFullPage(FrameworkElement host, CrosshairView page, double width, string name)
    {
        const double probe = 6000;
        host.Width = width;
        host.Measure(new Size(width, probe));
        host.Arrange(new Rect(0, 0, width, probe));
        host.UpdateLayout();
        // 页面根是 XAML 里那个 Grid：第 1 行标题、第 2 行纵向 ScrollViewer（离屏时视觉树还没挂上去，走逻辑树）。
        var scroller = (page.Content as Grid)?.Children.OfType<ScrollViewer>().FirstOrDefault();
        Assert.NotNull(scroller);
        var header = page.ActualHeight - scroller!.ActualHeight;
        var height = Math.Ceiling(header + scroller.ExtentHeight + 4);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var missing = scroller.ExtentHeight - scroller.ViewportHeight;
        Assert.True(missing <= 1, $"{name} 整页没排开，仍差 {missing:0} 逻辑像素");
        return Render(host, name);
    }

    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    private static void SelectMode(CrosshairView view, string mode)
    {
        var name = mode switch
        {
            "fire" => "CrosshairModeFire",
            "ads" => "CrosshairModeAds",
            _ => "CrosshairModeAlways",
        };
        ((RadioButton)view.FindName(name)).IsChecked = true;
    }

    private static void PumpMs(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>输出目录：可用 FPSTUNE_SHOTS_DIR 指到集成工作区，默认落在仓库 work 下。</summary>
    private static string ShotsDir()
    {
        var configured = Environment.GetEnvironmentVariable("FPSTUNE_SHOTS_DIR");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "shots-crosshair")
            : configured;
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>渲染 + 可读性守卫：可见文字不小于 13 号（与既有页面守卫同一条线）。</summary>
    private static string Render(FrameworkElement element, string name)
    {
        var small = ReadabilityAndFramesUiTests.Descendants(element).OfType<TextBlock>()
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.FontSize < 13)
            .Select(t => $"{t.Text}({t.FontSize})").Distinct().ToArray();
        Assert.True(small.Length == 0, $"{name} 有小于 13 号的可见文字：" + string.Join(" | ", small.Take(6)));

        var frame = new DispatcherFrame();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(visual);
        var path = Path.Combine(ShotsDir(), name + ".png");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return path;
    }
}
