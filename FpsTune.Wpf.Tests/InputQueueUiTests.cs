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
/// 维护页「键鼠缓冲区深度」的写入前二次确认（真对话框 + 真分支）与离屏渲染证据。
/// 三条分支都要落到「写不写、写的是哪个值」，因为这条路径会改系统注册表。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class InputQueueUiTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Maintenance_input_queue_confirms_before_writing()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(InputQueueUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var writes = new List<(int Keyboard, int Mouse)>();
            InputQueueWriter.WriteOverride = (keyboard, mouse) =>
            {
                writes.Add((keyboard, mouse));
                return Task.FromResult<IReadOnlyList<OptimizationApplyResult>>([]);
            };
            var settings = SettingsService.Current;
            var (keyboard0, mouse0, rate0) = (settings.KeyboardQueueDepth, settings.MouseQueueDepth, settings.MousePollingRate);
            DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
            try
            {
                // 设置里是「键盘 104 / 鼠标 50」：鼠标 50 在 1000 Hz 下低于推荐 96，写入前必须确认。
                settings.KeyboardQueueDepth = 104;
                settings.MouseQueueDepth = 50;
                settings.MousePollingRate = 1000;

                var view = new MaintenanceView();
                var window = new Window { Width = 1180, Height = 780, Content = view };
                app.MainWindow = window;
                DlssFeedbackTests.PrepareBackgroundWindow(window);
                window.Show();
                view.InputNav.IsChecked = true;
                view.UpdateLayout();

                Assert.Equal("104", view.KeyboardDepthBox.Text);
                Assert.Equal("56", view.MouseDepthBox.Text);
                Assert.Equal(Visibility.Visible, view.InputQueueWarning.Visibility);
                Assert.Equal(Visibility.Visible, view.InputQueueApplyButton.Visibility);

                // ① 取消：一条都不写，设置也不动。
                ClickDialog("CancelButton");
                Run(view.ApplyInputQueueAsync());
                Assert.Empty(writes);
                Assert.Equal(string.Empty, view.InputQueueStatus.Text);

                // ② 仍然用 56：按输入框里的值写。
                var keepText = Str.T("Str.InputQueueConfirmKeep", 56);
                ClickDialog("SecondaryButton", dialog =>
                {
                    Assert.IsType<AppDialogWindow>(dialog);
                    Assert.Equal(Visibility.Visible, ((Button)dialog.FindName("SecondaryButton")).Visibility);
                    Assert.Equal(keepText, ((Button)dialog.FindName("SecondaryButton")).Content);
                });
                Run(view.ApplyInputQueueAsync());
                Assert.Equal(new[] { (104, 56) }, writes);
                Assert.Equal(Str.T("Str.InputQueueApplied"), view.InputQueueStatus.Text);

                // ③ 改用推荐值：键盘与鼠标都换成推荐值（各 96），输入框同步显示。
                writes.Clear();
                view.MouseDepthBox.Text = "48";
                var expectedKeyboard = InputQueuePolicy.Normalize(InputQueuePolicy.KeyboardRecommendedDepth);
                var expectedMouse = InputQueuePolicy.Normalize(InputQueuePolicy.RecommendedDepth(1000));
                ClickDialog("ConfirmButton");
                Run(view.ApplyInputQueueAsync());
                Assert.Equal(new[] { (expectedKeyboard, expectedMouse) }, writes);
                Assert.Equal(expectedKeyboard.ToString(), view.KeyboardDepthBox.Text);
                Assert.Equal(expectedMouse.ToString(), view.MouseDepthBox.Text);
                Assert.Equal(Visibility.Collapsed, view.InputQueueWarning.Visibility);

                // ④ 不低于推荐值就不弹确认：直接写。
                writes.Clear();
                view.MouseDepthBox.Text = "96";
                DialogService.PrepareWindowForTest = dialog =>
                {
                    DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() => dialog.Close()));
                };
                Run(view.ApplyInputQueueAsync());
                Assert.Equal(new[] { (expectedKeyboard, 96) }, writes);
                DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;

                // ⑤ 反馈说明：关键字段都在，且没有漏配的字符串键。
                view.MouseDepthBox.Text = "56";   // 截图里带上低值警告
                ScreenshotMaintenance(view, "light");
                ScreenshotMaintenance(view, "dark");
                ScreenshotConfirmDialog(app, window, "light");
                ScreenshotConfirmDialog(app, window, "dark");

                settings.KeyboardQueueDepth = 200;
                settings.MouseQueueDepth = 152;
                settings.MousePollingRate = 2000;
                DiagnosticReportExporter.BaseDirOverride = Path.Combine(root, "diagnostic");
                DiagnosticReportExporter.DeviceProbeOverride = () => ("Fixture Keyboard", "Fixture Mouse");
                var note = DiagnosticReportExporter.BuildInputFeedbackText();
                Assert.Contains("200", note);
                Assert.Contains("152", note);
                Assert.Contains("2000", note);
                Assert.Contains("Fixture Keyboard", note);
                Assert.Contains("Fixture Mouse", note);
                Assert.DoesNotContain("!Str.", note);
                var path = DiagnosticReportExporter.ExportInputFeedback();
                Assert.True(File.Exists(path));
                Assert.StartsWith(Path.Combine(root, "diagnostic", "feedback"), path);

                // ⑥ 服务体检行：运行状态与启动类型两列都在，且文案全部解析。
                var row = DetectionService.BuildServiceCheck("SysMain", "WSearch");
                Assert.Contains("SysMain", row.Message);
                Assert.Contains("WSearch", row.Message);
                Assert.Contains(Str.T("Str.ServiceRunningState"), row.Message);
                Assert.Contains(Str.T("Str.ServiceStartType"), row.Message);
                Assert.DoesNotContain("!Str.", row.Message);

                window.Close();
            }
            finally
            {
                InputQueueWriter.WriteOverride = null;
                DialogService.PrepareWindowForTest = null;
                DiagnosticReportExporter.DeviceProbeOverride = null;
                DiagnosticReportExporter.BaseDirOverride = null;
                settings.KeyboardQueueDepth = keyboard0;
                settings.MouseQueueDepth = mouse0;
                settings.MousePollingRate = rate0;
            }
        });
    }

    /// <summary>在真对话框窗口上点指定按钮（可选：点之前先断言对话框本身）。</summary>
    private static void ClickDialog(string buttonName, Action<AppDialogWindow>? beforeClick = null)
        => DialogService.PrepareWindowForTest = dialog =>
        {
            DlssFeedbackTests.PrepareBackgroundWindow(dialog);
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                var real = Assert.IsType<AppDialogWindow>(dialog);
                beforeClick?.Invoke(real);
                var button = (Button)real.FindName(buttonName);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
        };

    private static void Run(Task task)
    {
        DlssFeedbackTests.PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void ScreenshotMaintenance(MaintenanceView view, string theme)
    {
        ThemeManager.SetMode(theme);
        WaitForTheme(theme);
        view.UpdateLayout();
        RenderTo(view, "maintenance-input-page-" + theme);
        RenderTo(view.InputSection, "maintenance-input-" + theme);
    }

    private static void ScreenshotConfirmDialog(Application app, Window owner, string theme)
    {
        ThemeManager.SetMode(theme);
        WaitForTheme(theme);
        var dialog = new AppDialogWindow(Str.T("Str.InputQueueConfirmTitle"),
            Str.T("Str.InputQueueConfirmBody", 56, InputQueuePolicy.CoverageMs(56, 1000),
                InputQueuePolicy.RecommendedDepth(1000)),
            Str.T("Str.InputQueueConfirmUseRecommended"), Str.T("Str.InputQueueConfirmKeep", 56),
            Str.T("Str.Cancel")) { Owner = owner };
        DlssFeedbackTests.PrepareBackgroundWindow(dialog);
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            RenderTo((FrameworkElement)dialog.Content, "input-confirm-" + theme);
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void WaitForTheme(string theme)
    {
        var expected = (Color)ColorConverter.ConvertFromString(theme == "light" ? "#F7F9FC" : "#0C1015")!;
        DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)Application.Current.FindResource("AppBackdropBrush")).Color == expected);
    }

    private static void RenderTo(FrameworkElement element, string name)
    {
        var frame = new DispatcherFrame();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(visual);
        var dir = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "ref-align-297", "shots-input");
        Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
