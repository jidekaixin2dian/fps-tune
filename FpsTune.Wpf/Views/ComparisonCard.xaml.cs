using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 前后对照卡（0.5.0 M2）：显示三段式进度、增益结论与判定徽标，回滚按钮走
/// ComparisonPlanner 的备份快照还原链。应用动作由宿主页面（优化页）提供——弹既有审阅窗确认后才写系统。
/// </summary>
public partial class ComparisonCard : UserControl
{
    private ComparisonResult? _result;
    private bool _running;

    public ComparisonCard()
    {
        InitializeComponent();
    }

    /// <summary>对照流程是否在跑（宿主页面据此拒绝重复启动）。</summary>
    internal bool IsRunning => _running;

    /// <summary>应用回调：宿主弹审阅窗确认后调 NativeOptimizationEngine，返回逐项回执与备份快照。</summary>
    internal delegate Task<ComparisonApplyOutcome> ApplyDelegate(
        IReadOnlyList<string> items, string? gamePath, CancellationToken ct);

    /// <summary>开始一次对照。运行中重复调用被拒绝；完成后结果留在卡上直到下一次开始。</summary>
    internal async void Start(ComparisonRequest request,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ComparisonApplyOutcome>> apply,
        Action? onFinished = null)
    {
        if (_running) return;
        _running = true;
        _result = null;
        RollbackButton.Visibility = Visibility.Collapsed;
        RollbackButton.Content = null;
        ResultPanel.Visibility = Visibility.Collapsed;
        ResetStages();
        try
        {
            var result = await ComparisonPlanner.RunAsync(request, CancellationToken.None,
                new StageProgress(this, Dispatcher), apply).ConfigureAwait(true);
            if (result.Ok && result.Verdict is not null) ShowVerdict(result);
            else ShowFailure(result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Str.T("Str.ComparisonCancelled");
        }
        catch (Exception ex)
        {
            StatusText.Text = Str.T("Str.ComparisonFailed", PrivacyScrub.Sanitize(ex.Message));
        }
        finally
        {
            _running = false;
            onFinished?.Invoke();
        }
    }

    /// <summary>结论卡上的一键回滚：按本流程自己的备份快照还原。</summary>
    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null || _running) return;
        RollbackButton.IsEnabled = false;
        StatusText.Text = Str.T("Str.ComparisonRollingBack");
        try
        {
            var rollback = await Task.Run(() => ComparisonPlanner.Rollback(result.BackupFile, result.AppliedIds))
                .ConfigureAwait(true);
            if (rollback.Ok)
            {
                RollbackButton.Content = Str.T("Str.ComparisonRolledBack");
                StatusText.Text = Str.T("Str.ComparisonRollbackDone", rollback.RestoredCount);
                SetVerdictBadge(GainDecision.Rollback, "Str.ComparisonActionRolledBack");
            }
            else
            {
                RollbackButton.IsEnabled = true;
                StatusText.Text = Str.T("Str.ComparisonRollbackFailed", PrivacyScrub.Sanitize(rollback.Error ?? ""));
            }
        }
        catch (Exception ex)
        {
            RollbackButton.IsEnabled = true;
            StatusText.Text = Str.T("Str.ComparisonRollbackFailed", PrivacyScrub.Sanitize(ex.Message));
        }
    }

    // ---- 渲染 ----

    /// <summary>渲染一次完成的对照结论（宿主与测试共用）。</summary>
    internal void ShowVerdict(ComparisonResult result)
    {
        _result = result;
        HighlightStage(ComparisonStage.Verdict);
        RenderVerdict(result);
    }

    /// <summary>渲染对照失败态（宿主与测试共用）。</summary>
    internal void ShowFailure(ComparisonResult result) => RenderFailure(result);

    /// <summary>高亮某个阶段（宿主与测试共用）。</summary>
    internal void ShowStage(ComparisonStage stage) => HighlightStage(stage);

    /// <summary>同步 IProgress：在调用线程直接高亮阶段（BeginInvoke 回 UI）。</summary>
    private sealed class StageProgress(ComparisonCard card, Dispatcher dispatcher) : IProgress<ComparisonStage>
    {
        public void Report(ComparisonStage stage) =>
            dispatcher.BeginInvoke(() => card.HighlightStage(stage));
    }

    private void HighlightStage(ComparisonStage stage)
    {
        ResetStages();
        var accent = (Brush)FindResource("AccentBrush");
        var dot = stage switch
        {
            ComparisonStage.Before => StageBeforeDot,
            ComparisonStage.Apply => StageApplyDot,
            ComparisonStage.Cooldown => StageCooldownDot,
            ComparisonStage.After => StageAfterDot,
            _ => StageVerdictDot,
        };
        var text = stage switch
        {
            ComparisonStage.Before => StageBeforeText,
            ComparisonStage.Apply => StageApplyText,
            ComparisonStage.Cooldown => StageCooldownText,
            ComparisonStage.After => StageAfterText,
            _ => StageVerdictText,
        };
        dot.Background = accent;
        text.Foreground = accent;
        StatusText.Text = Str.T(StageTextKey(stage) + "Status");
    }

    private static string StageTextKey(ComparisonStage stage) => stage switch
    {
        ComparisonStage.Before => "Str.ComparisonStageBefore",
        ComparisonStage.Apply => "Str.ComparisonStageApply",
        ComparisonStage.Cooldown => "Str.ComparisonStageCooldown",
        ComparisonStage.After => "Str.ComparisonStageAfter",
        _ => "Str.ComparisonStageVerdict",
    };

    private void ResetStages()
    {
        var border = (Brush)FindResource("BorderBrush");
        var text = (Brush)FindResource("TextSecondaryBrush");
        foreach (var (dot, label) in new (Border, TextBlock)[]
        {
            (StageBeforeDot, StageBeforeText), (StageApplyDot, StageApplyText),
            (StageCooldownDot, StageCooldownText), (StageAfterDot, StageAfterText),
            (StageVerdictDot, StageVerdictText),
        })
        {
            dot.Background = border;
            label.Foreground = text;
        }
    }

    private void RenderFailure(ComparisonResult result)
    {
        HighlightFailedStage(result.ReasonCode);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = Str.T("Str.ComparisonFailed", PrivacyScrub.Sanitize(result.Error ?? ""));
        RollbackButton.Visibility = Visibility.Collapsed;
    }

    private void HighlightFailedStage(string? reasonCode)
    {
        var danger = (Brush)FindResource("DangerBrush");
        var dot = reasonCode switch
        {
            "no-permission" or "no-process" or "no-sampler" => StageBeforeDot,
            "apply-failed" or "no-backup" => StageApplyDot,
            _ => StageVerdictDot,
        };
        dot.Background = danger;
    }

    private void RenderVerdict(ComparisonResult result)
    {
        var verdict = result.Verdict!;
        ResultPanel.Visibility = Visibility.Visible;
        SetVerdictBadge(verdict.Decision, verdict.ReasonKey);

        var gain = GainVerdict.FormatGain(verdict.Analysis);
        GainText.Text = verdict.Analysis.AvgDiffLowerPct is { } lo && verdict.Analysis.AvgDiffUpperPct is { } hi
            ? Str.T("Str.GainCi", gain, lo, hi)
            : gain;
        AvgText.Text = verdict.Analysis.AvgBase is { } b && verdict.Analysis.AvgTest is { } a
            ? string.Create(CultureInfo.InvariantCulture, $"{b:0.#} → {a:0.#}")
            : "—";
        P1LowText.Text = FormatPercentDiff(verdict.Analysis.P1DiffPct);
        P99Text.Text = FormatPercentDiff(verdict.Analysis.P99DiffPct);
        SampleNoteText.Text = Str.T("Str.ComparisonSampleNote",
            verdict.SegmentsBefore, verdict.SegmentsAfter,
            verdict.Analysis.RoundsBase + verdict.Analysis.RoundsTest);
        AdviceText.Text = Str.T(verdict.Decision switch
        {
            GainDecision.Keep => "Str.ComparisonAdviceKeep",
            GainDecision.Rollback => "Str.ComparisonAdviceRollback",
            _ => "Str.ComparisonAdviceInsufficient",
        });

        RollbackButton.Visibility = verdict.Decision == GainDecision.Keep ? Visibility.Collapsed : Visibility.Visible;
        RollbackButton.Content = Str.T("Str.ComparisonRollbackNow");
        RollbackButton.IsEnabled = true;
        StatusText.Text = Str.T("Str.ComparisonDone");
    }

    private void SetVerdictBadge(GainDecision decision, string reasonKey)
    {
        VerdictText.Text = Str.T(reasonKey);
        // 配色只用既有主题键：保留=强调色，回滚=危险色，证据不足=次级文字。
        var (fill, stroke) = decision switch
        {
            GainDecision.Keep => ("AccentSoftBrush", "AccentBrush"),
            GainDecision.Rollback => ("SurfaceBrush", "DangerBrush"),
            _ => ("SurfaceBrush", "TextSecondaryBrush"),
        };
        VerdictBadge.Background = (Brush)FindResource(fill);
        VerdictIcon.Stroke = (Brush)FindResource(stroke);
        VerdictText.Foreground = (Brush)FindResource(stroke);
        VerdictIcon.Data = Geometry.Parse(decision switch
        {
            GainDecision.Keep => "M1.5,7.5 L6,12 L12.5,4",
            GainDecision.Rollback => "M7,2.5 L2.5,7 L7,11.5 M2.5,7 L13,7",
            _ => "M7,2.5 A1.5,1.5 0 1 1 7,5.5 M7,7.5 L7,11.5",
        });
    }

    private static string FormatPercentDiff(double? diff) => diff is null
        ? "—"
        : (diff.Value >= 0 ? "+" : "") + diff.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
