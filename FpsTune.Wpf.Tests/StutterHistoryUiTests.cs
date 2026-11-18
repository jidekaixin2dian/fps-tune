using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class StutterHistoryUiTests
{
    [Fact]
    public void Stutter_history_is_reachable_and_refreshes_legacy_records()
    {
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        {
            DlssFeedbackTests.RunIsolated(testType: typeof(StutterHistoryUiTests));
            return;
        }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var main = new MainWindow { Width = 1180, Height = 720 };
            app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main);
            main.Show();
            try
            {
                var home = Assert.IsType<ConsoleView>(main.PageHost.Content);
                ThemeManager.SetModeInstant("light");
                OpenFrom((Button)home.FindName("HomeStutterHistoryButton"), dialog =>
                {
                    var refresh = (Button)dialog.FindName("RefreshButton");
                    var empty = (TextBlock)dialog.FindName("EmptyText");
                    DlssFeedbackTests.PumpUntil(() => refresh.IsEnabled && !string.IsNullOrWhiteSpace(empty.Text));
                    Assert.Equal(Str.T("Str.StutterHistoryEmpty"), empty.Text);
                    Directory.CreateDirectory(StutterEvidence.EvidenceRoot);
                    File.WriteAllText(Path.Combine(StutterEvidence.EvidenceRoot, "stutter-20260101-010203.json"), """
                        {"gameName":"测试游戏","at":"2026-01-01T01:02:03+08:00","stutterFramesMs":[66.7,45],"medianMs":16.7,"fps":60,"p1Low":45,"gpuName":"Fixture GPU","gpuUtilizationPercent":82,"backgroundTop":["chrome.exe 12%"],"hagsState":"on"}
                        """);
                    refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    DlssFeedbackTests.PumpUntil(() => refresh.IsEnabled);
                    Assert.Equal("测试游戏", ((TextBlock)dialog.FindName("GameText")).Text);
                    Assert.Contains("ms", ((TextBlock)dialog.FindName("PeakText")).Text);
                    Assert.Contains("chrome.exe", ((TextBlock)dialog.FindName("BackgroundText")).Text);
                    Assert.Equal(Visibility.Collapsed, empty.Visibility);
                    RenderMinimum(dialog, "light", "#F7F9FC");
                });

                ThemeManager.SetModeInstant("dark");
                main.NavigateTo("session");
                var session = Assert.IsType<SessionView>(main.PageHost.Content);
                OpenFrom((Button)session.FindName("SessionStutterHistoryButton"), dialog =>
                {
                    var list = (ListBox)dialog.FindName("HistoryList");
                    DlssFeedbackTests.PumpUntil(() => list.Items.Count > 0);
                    Assert.Single(list.Items);
                    Assert.NotNull(list.SelectedItem);
                    RenderMinimum(dialog, "dark", "#0C1015");
                });
            }
            finally { main.Close(); }
        });
    }

    private static void RenderMinimum(StutterHistoryWindow dialog, string theme, string expectedColor)
    {
        dialog.Width = dialog.MinWidth;
        dialog.Height = dialog.MinHeight;
        var expected = (Color)ColorConverter.ConvertFromString(expectedColor)!;
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(dialog.Background).Color);
        dialog.UpdateLayout();
        ReadabilityAndFramesUiTests.Render(Assert.IsAssignableFrom<FrameworkElement>(dialog.Content),
            "stutter-history-min-" + theme);
    }

    private static void OpenFrom(Button button, Action<StutterHistoryWindow> verify)
    {
        StutterHistoryWindow? opened = null;
        Exception? error = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<StutterHistoryWindow>().FirstOrDefault(w => w.IsVisible) is not { } window)
                return;
            timer.Stop();
            opened = window;
            try { verify(window); }
            catch (Exception ex) { error = ex; }
            finally { window.Close(); }
        };
        timer.Start();
        try { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { timer.Stop(); }
        Assert.NotNull(opened);
        Assert.Null(error);
    }
}
