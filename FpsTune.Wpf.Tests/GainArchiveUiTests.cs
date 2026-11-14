using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 游戏库「收益档案」卡的真渲染：空档案、有数据（摘要 + 趋势）、跨机指纹提示，深/浅主题。
/// 渲染结果存 work/readability-frames/ui/gain-archive-*.png。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class GainArchiveUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private const string Fingerprint = "ui-test-gpu|ui-test-driver|ui-test-cpu|16";

    private static GainArchiveEntry Entry(string gameId, string items, double diff, string fingerprint = Fingerprint) =>
        new(gameId, DateTimeOffset.Now.AddMinutes(-Random.Shared.Next(1, 90)), [items], "keep",
            100, 100 + diff, diff, diff - 1, diff + 1, 2, -3, 6, 6, 60, null, false, fingerprint);

    [Fact]
    public void Gain_archive_card_renders_summary_trend_and_foreign_hint()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(GainArchiveUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            GainArchiveService.FingerprintOverride = () => Fingerprint;
            var gamePath = Path.Combine(root, "UiTestGame.exe");
            File.WriteAllText(gamePath, "fixture");
            var profile = StateStore.AddGame(gamePath);
            var gameId = Path.GetFileNameWithoutExtension(gamePath);

            var main = new MainWindow { Width = 1180, Height = 720 };
            app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main);
            main.Show();
            main.NavigateTo("games");
            var library = (GameLibraryView)((ContentControl)main.FindName("PageHost")).Content;
            var games = (ListBox)library.FindName("GameList");
            try
            {
                DlssFeedbackTests.PumpUntil(() => games.Items.Count >= 1);

                // 1) 空档案：提示怎么产生记录。
                games.SelectedItem = games.Items.Cast<object>().First();
                var summary = (TextBlock)library.FindName("GainSummaryText");
                var hint = (TextBlock)library.FindName("GainHintText");
                DlssFeedbackTests.PumpUntil(() => !string.IsNullOrEmpty(summary.Text));
                Assert.Contains("还没有对照记录", summary.Text);
                Assert.Equal("", hint.Text);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); main.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), $"gain-archive-empty-{theme}");
                }

                // 2) 有数据：摘要（次数/最佳收益/最近时间）+ 本机指纹不提示。
                GainArchiveService.Append(Entry(gameId, "sysmain", 5.5));
                GainArchiveService.Append(Entry(gameId, "mmcss-games", 3.2));
                library.GetType().GetMethod("RefreshGainArchive",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(library, [library.FindName("GameList") is ListBox list ? list.SelectedItem : null]);
                main.UpdateLayout();
                Assert.Contains("2 次对照", summary.Text);
                Assert.Contains("+5.5%", summary.Text);
                Assert.Equal("", hint.Text);
                var chart = (Canvas)library.FindName("GainChart");
                Assert.True(chart.Children.Count > 0, "有数据时应画出趋势折线");
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); main.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), $"gain-archive-data-{theme}");
                }

                // 3) 全部条目来自别的机器：如实提示只作参考。
                GainArchiveService.FingerprintOverride = () => "another-machine|d|c|16";
                library.GetType().GetMethod("RefreshGainArchive",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(library, [library.FindName("GameList") is ListBox list2 ? list2.SelectedItem : null]);
                main.UpdateLayout();
                Assert.Contains("另一台机器", hint.Text);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); main.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render((FrameworkElement)main.FindName("ChromeRoot"), $"gain-archive-foreign-{theme}");
                }
            }
            finally
            {
                GainArchiveService.FingerprintOverride = null;
                try { GameLibraryService.Remove(profile); } catch { }
                main.Close();
            }
        });
    }
}
