using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 0.3.7 新优化项（Windows 11 电源模式、FilterKeys 防误触）与开局准备新区块的真实渲染守卫：
/// 真实 WPF 窗口、屏外不激活、深浅两主题、PNG 落 work 供人工复核。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class PreparationEngineUiTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void New_optimization_items_and_preparation_sections_render_in_both_themes()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationEngineUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var window = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                window.NavigateTo("opt"); window.UpdateLayout();
                var view = (OptimizeView)((ContentControl)window.FindName("PageHost")).Content;
                var items = view.ItemsView.Cast<OptimizationItemViewModel>().ToList();
                var filterKeys = items.Single(i => i.Id == "filter-keys-off");
                // 电源模式项只在支持现代滑块的系统上出现；CI 机器常常不支持，按实际能力断言而不是硬编码。
                var overlaySupported = OptimizationCatalog.Items.Single(i => i.Id == "power-overlay-perf").Available;
                var overlay = items.SingleOrDefault(i => i.Id == "power-overlay-perf");
                Assert.Equal(overlaySupported, overlay is not null);
                Assert.True(filterKeys.Available);
                if (overlay is not null) Assert.NotEqual(overlay.Name, overlay.Description);
                var list = (ListBox)view.FindName("ItemList");
                foreach (var mode in new[] { "dark", "light" })
                {
                    ThemeManager.SetMode(mode); window.UpdateLayout();
                    // 离屏窗口的 HWND 表面不随主题重合成，整页证据用视图级截图（铺窗口底色）。
                    Render(view, $"opt-page-{mode}", backdropKey: "ConsoleBackgroundBrush");
                    if (overlay is not null) RenderRow(window, list, overlay, $"power-overlay-{mode}");
                    RenderRow(window, list, filterKeys, $"filter-keys-{mode}");
                }
                ThemeManager.SetMode("dark");
            }
            finally { window.Close(); }

            foreach (var mode in new[] { "dark", "light" })
            {
                var prep = new GamePreparationWindow();
                DlssFeedbackTests.PrepareBackgroundWindow(prep); prep.Show();
                try
                {
                    // 显示枚举完成后保存/准备按钮才可用；此时新区块已就绪。
                    DlssFeedbackTests.PumpUntil(() => prep.SaveButton.IsEnabled);
                    Assert.True(prep.OptimizationsCheck.Content is string s && s.Length > 0);
                    Assert.Equal(ItemCatalog.All.First(i => i.Id == "power-ultimate").DisplayName, prep.PowerPlanCheck.Content);
                    Assert.True(prep.FrameLimitCheck.IsEnabled, "有游戏上下文时限帧应可选");
                    ThemeManager.SetMode(mode); prep.UpdateLayout();
                    // 离屏窗口的 HWND 表面不随主题重合成；改截窗口内容元素并铺窗口底色。
                    Render((FrameworkElement)prep.Content, $"preparation-{mode}", backdropKey: "ConsoleBackgroundBrush");
                    // 新增限帧输入行默认在折叠区下方：滚动到底再补一张。
                    var scroll = ReadabilityAndFramesUiTests.Descendants(prep).OfType<ScrollViewer>().First();
                    scroll.ScrollToEnd(); prep.UpdateLayout();
                    Render((FrameworkElement)prep.Content, $"preparation-{mode}-frame", backdropKey: "ConsoleBackgroundBrush");
                }
                finally { prep.Close(); }
            }
            ThemeManager.SetMode("dark");
        });
    }

    [Fact]
    public void Timer_boost_card_and_background_check_card_render_in_both_themes()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationEngineUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var window = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                foreach (var mode in new[] { "dark", "light" })
                {
                    ThemeManager.SetMode(mode);
                    // 设置页 → 游戏卡：计时器开关与状态行
                    window.NavigateTo("settings"); window.UpdateLayout();
                    var settings = (FrameworkElement)((ContentControl)window.FindName("PageHost")).Content;
                    ((RadioButton)settings.FindName("SettingsNav2")).IsChecked = true; window.UpdateLayout();
                    DlssFeedbackTests.PumpUntil(() => ((CheckBox)settings.FindName("TimerBoostCheck")).IsChecked != null);
                    var timerCard = (FrameworkElement)((CheckBox)settings.FindName("TimerBoostCheck")).Parent;
                    Render(timerCard, $"timer-boost-{mode}", backdropKey: "ConsoleBackgroundBrush");
                    // 游戏自动化卡：两个 opt-in 开关
                    var prepCheck = (CheckBox)settings.FindName("AutoPreparationCheck");
                    Assert.NotNull(settings.FindName("AutoRefreshCheck"));
                    Render((FrameworkElement)prepCheck.Parent, $"auto-session-{mode}", backdropKey: "ConsoleBackgroundBrush");
                    // 维护页 → 进程区：后台资源检查卡
                    window.NavigateTo("tools"); window.UpdateLayout();
                    var tools = (FrameworkElement)((ContentControl)window.FindName("PageHost")).Content;
                    ((RadioButton)tools.FindName("ProcessesNav")).IsChecked = true; window.UpdateLayout();
                    var bgButton = ReadabilityAndFramesUiTests.Descendants(tools).OfType<Button>().First(b => b.Name == "BgCheckButton");
                    var bgCard = (FrameworkElement)((DockPanel)bgButton.Parent).Parent;
                    Render(bgCard, $"bg-check-{mode}", backdropKey: "ConsoleBackgroundBrush");
                }
                ThemeManager.SetMode("dark");
            }
            finally { window.Close(); }
        });
    }

    private static void RenderRow(MainWindow window, ListBox list, OptimizationItemViewModel item, string name)
    {
        list.ScrollIntoView(item); window.UpdateLayout();
        // BringIntoView 是异步滚动动画：等容器完成布局再截，避免截到空白区域。
        DlssFeedbackTests.PumpUntil(() =>
            list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row
            && row.ActualWidth > 10 && row.ActualHeight > 10);
        var realized = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)!;
        var content = ReadabilityAndFramesUiTests.Descendants(realized).OfType<Border>().FirstOrDefault();
        // 页面根背景是透明画刷（窗口层才画背景），元素级截图需先铺卡片底色再画内容。
        Render((FrameworkElement?)content ?? realized, name, backdropKey: "SurfaceBrush");
    }

    private static void Render(FrameworkElement element, string name, string backdropKey = "SurfaceBrush")
    {
        element.UpdateLayout();
        // 主题刷子换色后重绘排在 Render 优先级；UpdateLayout 不会 flush，
        // 直接截图会拿到旧配色的合成结果。把队列排空两次再截。
        for (var i = 0; i < 2; i++)
            element.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var brush = new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds };
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            if (element.TryFindResource(backdropKey) is Brush surface)
                dc.DrawRectangle(surface, null, bounds);
            dc.DrawRectangle(brush, null, bounds);
        }
        bitmap.Render(visual);
        var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "preparation-engine037", "ui");
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
