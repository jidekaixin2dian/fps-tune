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
/// 后台与自启页的真渲染用例：离屏窗口、深/浅两套主题、最小窗口各出一张图，
/// 并核对导航注册、两张卡片的行数与「加入审阅」按钮状态。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class StartupAuditUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "startup-audit", "ui");

    [Fact]
    public void Startup_page_renders_entries_and_services_in_both_themes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupAuditUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            using var fixture = Fixture();
            var main = new MainWindow { Width = 1360, Height = 860 };
            app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main);
            main.Show();
            try
            {
                main.NavigateTo("startup");
                main.UpdateLayout();
                Assert.IsType<StartupAuditView>(main.PageHost.Content);
                var view = (StartupAuditView)main.PageHost.Content;
                Assert.True(WaitUntil(() => view.StartupItems.ItemsSource is not null
                                            && view.ServiceGroups.ItemsSource is not null, 5000));

                Assert.Equal(4, ((System.Collections.ICollection)view.StartupItems.ItemsSource!).Count);
                Assert.Equal(2, ((System.Collections.ICollection)view.ServiceGroups.ItemsSource!).Count);
                Assert.False(view.ReviewButton.IsEnabled);
                Assert.Equal(Str.T("Str.StartupReviewNone"), view.ReviewButton.Content);

                // 超长命令不能把行撑出可视区：每行右侧的动作按钮都必须落在页面宽度之内。
                foreach (var button in Descendants<Button>(view.StartupItems).Where(b => b.Content is string))
                {
                    var bounds = button.TransformToAncestor(view).TransformBounds(
                        new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Assert.InRange(bounds.Right, 0, view.ActualWidth);
                }

                // 服务行左侧那一列不能留空：能勾选的放复选框，不能勾选的放建议图标。
                var checkboxes = Descendants<CheckBox>(view.ServiceGroups).Count(c => c.Visibility == Visibility.Visible);
                var icons = Descendants<System.Windows.Shapes.Path>(view.ServiceGroups).Count(p => (p.Tag as string) == "AdviceIcon" && p.Visibility == Visibility.Visible);
                Assert.Equal(1, checkboxes);   // 仅 SysMain（WSearch 已禁用，Audiosrv 是「别关」）
                Assert.Equal(2, icons);        // WSearch + Audiosrv 各一个

                foreach (var theme in new[] { "light", "dark" })
                {
                    SettingsService.Current.ThemeMode = theme;
                    ThemeManager.SetMode(theme);
                    main.UpdateLayout();
                    Render((FrameworkElement)main.Content, "startup-" + theme);
                }
            }
            finally
            {
                main.Close();
            }
        });
    }

    [Fact]
    public void Startup_page_keeps_its_layout_at_the_minimum_window_size()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(StartupAuditUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            using var fixture = Fixture();
            var main = new MainWindow { Width = 1180, Height = 720 };
            app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main);
            main.Show();
            try
            {
                main.NavigateTo("startup");
                main.UpdateLayout();
                var view = (StartupAuditView)main.PageHost.Content;
                Assert.True(WaitUntil(() => view.StartupItems.ItemsSource is not null, 5000));
                Assert.True(view.ActualWidth > 0 && view.ActualHeight > 0);
                foreach (var theme in new[] { "light", "dark" })
                {
                    SettingsService.Current.ThemeMode = theme;
                    ThemeManager.SetMode(theme);
                    main.UpdateLayout();
                    Render((FrameworkElement)main.Content, "startup-min-" + theme);
                }
            }
            finally
            {
                main.Close();
            }
        });
    }

    /// <summary>注入一份固定快照：不读本机注册表、也不读真实服务状态。</summary>
    private static FixtureScope Fixture()
    {
        StartupAuditService.SnapshotOverride = () => new StartupAuditSnapshot(
        [
            new StartupEntry("run:hkcu:Steam", "Steam", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent",
                StartupScope.CurrentUser, StartupOrigin.Run, true, @"C:\Program Files (x86)\Steam\steam.exe"),
            new StartupEntry("run:hkcu:OldSetup", "OldSetup", @"C:\Users\me\AppData\Local\Temp\pkg\setup.exe",
                StartupScope.CurrentUser, StartupOrigin.Run, true, @"C:\Users\me\AppData\Local\Temp\pkg\setup.exe"),
            // 真机上出现过的形态：一条没有空格的超长路径。行不能被它撑宽，否则右侧按钮会被挤出可视区。
            new StartupEntry("run:hkcu:EpicGamesLauncher", "EpicGamesLauncher",
                "\"C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win64\\EpicGamesLauncher.exe\" -silent -launchcontext",
                StartupScope.CurrentUser, StartupOrigin.Run, true,
                @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"),
            new StartupEntry("runonce:hklm:VendorUpdater", "VendorUpdater", @"C:\Program Files\Vendor\updater.exe /quiet",
                StartupScope.AllUsers, StartupOrigin.RunOnce, true, @"C:\Program Files\Vendor\updater.exe")
        ], 0, DateTimeOffset.Now);

        var definitions = ServiceAuditCatalog.All;
        StartupAuditService.ServicesOverride = () => new ServiceAuditSnapshot(
        [
            new ServiceAuditRow(definitions.First(d => d.Name == "SysMain"), ServiceStartType.Auto, true),
            new ServiceAuditRow(definitions.First(d => d.Name == "WSearch"), ServiceStartType.Disabled, false),
            new ServiceAuditRow(definitions.First(d => d.Name == "Audiosrv"), ServiceStartType.Auto, true)
        ], 0, DateTimeOffset.Now);
        return new FixtureScope();
    }

    private sealed class FixtureScope : IDisposable
    {
        public void Dispose()
        {
            StartupAuditService.SnapshotOverride = null;
            StartupAuditService.ServicesOverride = null;
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>等异步刷新落地；超时返回 false，由调用方断言（不吞掉失败）。</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Pump(50);
        }
        return condition();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, element.ActualWidth, element.ActualHeight)
            }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        Directory.CreateDirectory(Output);
        using var stream = File.Create(Path.Combine(Output, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
