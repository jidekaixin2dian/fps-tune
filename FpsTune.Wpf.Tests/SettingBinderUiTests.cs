using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 设置页「改-存-回滚」收敛后的验收：<see cref="SettingBinder"/> 的保存/回滚语义，
/// 以及设置页在保存失败时真的把内存与勾选态一起翻回去（真渲染、深浅两套主题、最小窗口）。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class SettingBinderUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "verify-cognition-3b", "ui");

    /// <summary>
    /// 纯逻辑：保存成功写盘；写不进去时把内存改回旧值，并把原因按脱敏口径返回。
    /// 失败用独占打开 settings.json 制造——和真机上杀软/同步盘占用是同一条路径。
    /// </summary>
    [Fact]
    public void Binder_rolls_back_memory_when_the_settings_file_cannot_be_written()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(SettingBinderUiTests)); return; }
        var root = Path.Combine(Path.GetTempPath(), "fpstune-binder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousRoot = UserDataPaths.RootOverride;
        try
        {
            UserDataPaths.RootOverride = root;
            SettingsService.Load();
            SettingsService.Current.MinimizeToTray = false;
            SettingsService.Save(SettingsService.Current);

            var saved = SettingBinder.PersistBool(s => s.MinimizeToTray, (s, v) => s.MinimizeToTray = v, true);
            Assert.True(saved.Ok, saved.Detail);
            Assert.True(SettingsService.Current.MinimizeToTray);
            using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "settings.json"))))
                Assert.True(json.RootElement.GetProperty("MinimizeToTray").GetBoolean());

            var file = Path.Combine(root, "settings.json");
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = SettingBinder.PersistBool(s => s.MinimizeToTray, (s, v) => s.MinimizeToTray = v, false);
                Assert.False(failed.Ok);
                Assert.False(string.IsNullOrWhiteSpace(failed.Detail));
                // 内存必须回到旧值：否则界面与磁盘就是两个故事。
                Assert.True(SettingsService.Current.MinimizeToTray);

                // 逻辑值（主题这类字符串字段）走同一条回滚路径。
                var theme = SettingBinder.PersistValue(s => s.ThemeMode, (s, v) => s.ThemeMode = v, "light");
                Assert.False(theme.Ok);
                Assert.NotEqual("light", SettingsService.Current.ThemeMode);

                // 不保存 settings.json 的开关（自启写注册表）也要能回滚：Attempt 只跑改动与还原。
                var launched = false;
                var attempted = SettingBinder.Attempt(() => { launched = true; throw new InvalidOperationException("denied"); },
                    () => launched = false);
                Assert.False(attempted.Ok);
                Assert.False(launched);
            }

            // 解除占用后同一个调用必须能成功，证明失败不是被吞掉的状态。
            Assert.True(SettingBinder.PersistBool(s => s.MinimizeToTray, (s, v) => s.MinimizeToTray = v, false).Ok);
            Assert.False(SettingsService.Current.MinimizeToTray);
        }
        finally
        {
            UserDataPaths.RootOverride = previousRoot;
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* 临时目录清理失败不影响结论 */ }
        }
    }

    /// <summary>
    /// 真渲染：设置页的开关在保存失败时把内存与勾选态一起翻回去，并给出可见提示；
    /// 成功路径写盘；最小窗口与深浅两套主题各出一张图。
    /// </summary>
    [Fact]
    public void Settings_page_rolls_back_failed_toggles_and_renders_in_both_themes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(SettingBinderUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var main = new MainWindow { Width = 1360, Height = 860 };
            app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main);
            main.Show();
            try
            {
                main.NavigateTo("settings");
                main.UpdateLayout();
                var view = Assert.IsType<SettingsView>(main.PageHost.Content);
                // 计时器与游戏自动化都在「游戏」这一节，先切过去（隐藏节里的控件不参与布局，断言会失真）。
                ((RadioButton)view.FindName("SettingsNav2")).IsChecked = true;
                main.UpdateLayout();
                var timerBoost = (CheckBox)view.FindName("TimerBoostCheck");
                var autoPrep = (CheckBox)view.FindName("AutoPreparationCheck");
                var autoRefresh = (CheckBox)view.FindName("AutoRefreshCheck");
                var autoAffinity = (CheckBox)view.FindName("AutoAffinityCheck");
                var timerStatus = (TextBlock)view.FindName("TimerBoostStatus");
                var autoSessionStatus = (TextBlock)view.FindName("AutoSessionStatus");

                // 成功路径：勾上就该落到 settings.json。
                timerBoost.IsChecked = true;
                Assert.True(SettingsService.Current.GameTimerBoostEnabled);
                Assert.False(SettingsService.Current.AutoPreparationEnabled);

                // 失败路径：settings.json 被独占打开，三个联动勾选框必须一起弹回并说清原因。
                using (File.Open(Path.Combine(root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    autoPrep.IsChecked = true;
                    Assert.False(SettingsService.Current.AutoPreparationEnabled);
                    Assert.False(autoPrep.IsChecked == true);
                    Assert.False(autoRefresh.IsChecked == true);
                    Assert.False(autoAffinity.IsChecked == true);
                    Assert.Equal(Visibility.Visible, autoSessionStatus.Visibility);
                    Assert.False(string.IsNullOrWhiteSpace(autoSessionStatus.Text));

                    timerBoost.IsChecked = false;
                    Assert.True(SettingsService.Current.GameTimerBoostEnabled);
                    Assert.True(timerBoost.IsChecked == true);
                    Assert.False(string.IsNullOrWhiteSpace(timerStatus.Text));
                }

                // 解除占用后同一批开关必须能真正保存（回滚只是把界面拉回真实状态，不是把功能锁死）。
                autoPrep.IsChecked = true;
                Assert.True(SettingsService.Current.AutoPreparationEnabled);
                timerBoost.IsChecked = false;
                Assert.False(SettingsService.Current.GameTimerBoostEnabled);
                using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "settings.json"))))
                    Assert.True(json.RootElement.GetProperty("AutoPreparationEnabled").GetBoolean());

                main.UpdateLayout();
                foreach (var theme in new[] { "light", "dark" })
                {
                    SettingsService.Current.ThemeMode = theme;
                    ThemeManager.SetMode(theme);
                    main.UpdateLayout();
                    Render((FrameworkElement)main.Content, "settings-binder-" + theme);
                }

                main.Width = 1180;
                main.Height = 720;
                main.UpdateLayout();
                Assert.True(timerBoost.ActualWidth > 0 && timerBoost.ActualHeight > 0);
                Render((FrameworkElement)main.Content, "settings-binder-min");
            }
            finally
            {
                main.Close();
            }
        });
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
