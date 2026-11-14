using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 会话页「延迟预算」卡的真渲染：无采样数据时的空态与有采样数据时的三段数值，深/浅主题。
/// 截图存 work/readability-frames/ui/session-budget-*.png。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class SessionBudgetUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    private static void WriteCaptureCsv(string root)
    {
        var dir = Path.Combine(root, "captures", "capture-ui-test");
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,ClickToPhotonLatency",
        };
        for (var i = 0; i < 120; i++)
            lines.Add($"game.exe,1,0x1,DXGI,0,0,0,Composed: Flip,10,{16.0 + (i % 3) * 0.4:0.000},2.5,3.0,1.0,14.0,{15.5:0.000},1.0,{4.0 + (i % 2):0.000},16.5,0.4,25.0");
        File.WriteAllLines(Path.Combine(dir, "frames.csv"), lines);
    }

    private static void OpenSessionPage(Application app, MainWindow main, out SessionView page, out ListBox games)
    {
        main.Show();
        main.NavigateTo("session");
        page = (SessionView)((ContentControl)main.FindName("PageHost")).Content;
        games = (ListBox)page.FindName("HistoryList")!;
    }

    [Fact]
    public void Session_budget_card_renders_empty_and_measured_states()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(SessionBudgetUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var main = new MainWindow { Width = 1180, Height = 780 };
            app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main);
            try
            {
                // 1) 空数据态。
                OpenSessionPage(app, main, out var page, out _);
                var summary = (TextBlock)page.FindName("BudgetSummaryText")!;
                main.Dispatcher.Invoke(() => { ((Button)page.FindName("BudgetRefreshButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
                DlssFeedbackTests.PumpUntil(() => !string.IsNullOrEmpty(summary.Text));
                Assert.Contains("还没有可用的采样数据", summary.Text);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); main.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), "session-budget-empty-" + theme);
                }

                // 2) 有数据：伪造 captures 目录，刷新后出现三段数值。
                WriteCaptureCsv(root);
                main.Dispatcher.Invoke(() => { ((Button)page.FindName("BudgetRefreshButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
                DlssFeedbackTests.PumpUntil(() => summary.Text.Contains("输入段"));
                Assert.Contains("帧生成段", summary.Text);
                Assert.Contains("显示段", summary.Text);
                Assert.Contains("GPU 忙", summary.Text);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); main.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), "session-budget-data-" + theme);
                }
            }
            finally
            {
                main.Close();
            }
        });
    }
}
