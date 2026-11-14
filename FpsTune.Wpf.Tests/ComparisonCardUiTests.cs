using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 前后对照卡的真渲染用例：三档判定徽标与结论数字、深/浅两套主题、最小窗口宽度、
/// 阶段进度与失败态。渲染结果存 work/comparison/ui/。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class ComparisonCardUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "comparison", "ui");

    private static CaptureRun Run(string csv) => new(
        "capture-test", DateTimeOffset.Now, 60, "comparison", 3600, 100, 62, 26, 12,
        null, null, csv, Path.ChangeExtension(csv, ".json"), null, null);

    private static ComparisonResult Verdict(GainDecision decision) => new(
        true, null, null,
        new GainVerdictResult(decision, decision switch
        {
            GainDecision.Keep => "Str.ComparisonVerdictKeep",
            GainDecision.Rollback => "Str.ComparisonVerdictRollback",
            _ => "Str.ComparisonVerdictInsufficient",
        }, GainVerdictTests_Analysis(decision), 6, 6),
        Run("before.csv"), Run("after.csv"), "backup.json", ["sysmain"], false, null);

    private static AbAnalysisResult GainVerdictTests_Analysis(GainDecision decision) => decision switch
    {
        GainDecision.Keep => new AbAnalysisResult(AbAnalysisCodes.VerdictConfirmed, true, [], 6, 6, 0, 0, 0, 0, 0, 0,
            100, 106, 6, 5.8, 2.1, 9.4, 3.1, -4.2, 1.4, 10, 0.02, 0.02, 0.95),
        GainDecision.Rollback => new AbAnalysisResult(AbAnalysisCodes.VerdictNegligible, true, [], 6, 6, 0, 0, 0, 0, 0, 0,
            100, 100.3, 0.3, 0.3, -0.2, 0.8, 0.1, 0.4, 0.05, 10, 0.01, 0.01, 0.95),
        _ => new AbAnalysisResult(AbAnalysisCodes.VerdictUncertain, true, [], 6, 6, 0, 0, 0, 0, 0, 0,
            100, 101, 1, 1, -1.5, 3.6, 0.2, 0.3, 0.1, 10, 0.02, 0.03, 0.95),
    };

    [Fact]
    public void Comparison_card_renders_verdicts_and_stages_in_both_themes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ComparisonCardUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            Directory.CreateDirectory(Output);
            var card = new ComparisonCard { Width = 640 };
            var host = new Border { Child = card, Background = (Brush)app.Resources["AppBackgroundBrush"] };
            var window = new Window
            {
                Content = host,
                Width = 700,
                Height = 520,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -2400,
                Top = -1400,
            };
            window.Show();
            try
            {
                // 阶段进度（改后采样中）+ 三档判定 × 深/浅。
                card.ShowStage(ComparisonStage.After);
                foreach (var theme in new[] { "light", "dark" })
                {
                    SettingsService.Current.ThemeMode = theme;
                    ThemeManager.SetMode(theme);
                    window.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render(card, "comparison-stage-after-" + theme);
                }

                foreach (var (name, decision) in new[]
                         {
                             ("keep", GainDecision.Keep),
                             ("rollback", GainDecision.Rollback),
                             ("insufficient", GainDecision.Insufficient),
                         })
                {
                    card.ShowVerdict(Verdict(decision));
                    // 徽标文字与动作按钮与判定一致。
                    var badge = FindDescendant<TextBlock>(card, b => b.Name == "VerdictText");
                    var button = FindDescendant<Button>(card, b => b.Name == "RollbackButton");
                    Assert.NotNull(badge);
                    Assert.NotEqual("—", badge!.Text);
                    if (decision == GainDecision.Keep)
                        Assert.Equal(Visibility.Collapsed, button!.Visibility);
                    else
                    {
                        Assert.Equal(Visibility.Visible, button!.Visibility);
                        Assert.True(button.IsEnabled);
                    }
                    foreach (var theme in new[] { "light", "dark" })
                    {
                        SettingsService.Current.ThemeMode = theme;
                        ThemeManager.SetMode(theme);
                        window.UpdateLayout();
                        ReadabilityAndFramesUiTests.Render(card, $"comparison-{name}-{theme}");
                    }
                }

                // 失败态（权限被拒）不显示结论区，状态文字给下一步。
                card.ShowFailure(new ComparisonResult(false,
                    "采样被系统拒绝：PresentMon 的 ETW 会话需要管理员权限。请以管理员身份运行后重试。",
                    "no-permission", null, null, null, null, [], false, null));
                var resultPanel = FindDescendant<Border>(card, b => b.Name == "ResultPanel");
                Assert.Equal(Visibility.Collapsed, resultPanel!.Visibility);
                foreach (var theme in new[] { "light", "dark" })
                {
                    SettingsService.Current.ThemeMode = theme;
                    ThemeManager.SetMode(theme);
                    window.UpdateLayout();
                    ReadabilityAndFramesUiTests.Render(card, $"comparison-failed-{theme}");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Comparison_card_rollback_action_stays_within_min_width()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ComparisonCardUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            Directory.CreateDirectory(Output);
            var card = new ComparisonCard { Width = 340 }; // 近似最小窗口下的卡片可用宽度
            var host = new Border { Child = card, Background = (Brush)app.Resources["AppBackgroundBrush"] };
            var window = new Window
            {
                Content = host,
                Width = 380,
                Height = 480,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -2400,
                Top = -1400,
            };
            window.Show();
            try
            {
                SettingsService.Current.ThemeMode = "light";
                ThemeManager.SetMode("light");
                card.ShowVerdict(Verdict(GainDecision.Rollback));
                window.UpdateLayout();
                // 「立即回滚」按钮必须落在卡宽之内，不能被挤出去。
                var button = FindDescendant<Button>(card, b => b.Name == "RollbackButton")!;
                var bounds = button.TransformToAncestor(card).TransformBounds(
                    new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                Assert.InRange(bounds.Right, 0, card.ActualWidth);
                ReadabilityAndFramesUiTests.Render(card, "comparison-rollback-min-light");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && match(typed)) return typed;
            var found = FindDescendant(child, match);
            if (found is not null) return found;
        }
        return null;
    }
}
