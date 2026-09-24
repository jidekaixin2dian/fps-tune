using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FpsTune.Wpf.Services;
using Path = System.IO.Path;

namespace FpsTune.Wpf.Views;

/// <summary>
/// A/B 实验室 2.0：向导式状态机驱动。步骤顺序、运行互斥、结果与会话关联、
/// 错误与中断恢复全部持久化在 experiment/wizard.json（ExperimentWizardStore）。
/// 编排为进程内 ExperimentRunner（输出 JSON 与旧 tuning-experiment.ps1 同构，状态文件互认）。
/// </summary>
public partial class AbExperimentView : UserControl
{
    private ExperimentWizardState _wizard = new();
    private bool _running;
    private bool _cancelRequested;
    private CancellationTokenSource? _runCancellation;

    public AbExperimentView()
    {
        InitializeComponent();
        Loaded += (_, _) => ReloadWizard();
    }

    // ---------- 向导状态 ----------

    private void ReloadWizard()
    {
        // 页面切换不会取消正在运行的脚本；不能在同一实例回到页面时把
        // 持久化的 RunningStep 当成“上次中断”，否则 UI 会与实际运行脱节。
        if (_running)
        {
            RebuildWizardUi();
            return;
        }
        _wizard = ExperimentWizardStore.Load();
        RebuildWizardUi();
        RefreshHistory();
        RawBox.Text = string.IsNullOrWhiteSpace(_wizard.LastRawOutput)
            ? Str.T("Str.WaitingForSample")
            : _wizard.LastRawOutput;
        ShowStateMessage();
    }

    private void ShowStateMessage()
    {
        if (_running)
            return;
        if (_wizard.IsFutureSchema)
            StatusText.Text = _wizard.CompatibilityError;
        else if (_wizard.LastError is { } err)
            StatusText.Text = "上次出现问题：" + err
                + (_wizard.LastErrorStep is { } failed ? $" 可重试：{WizardSteps.DisplayName(failed)}。" : " 可重试对应步骤。");
        else if (!_wizard.BaselineDone)
            StatusText.Text = Str.T("Str.AbReadySimulate");
        else if (_wizard.Groups.Count < WizardSteps.Groups.Length)
            StatusText.Text = $"基线已完成（平均 {_wizard.BaselineAvgFps:0.#} FPS）。继续测试候选组，或生成报告。";
        else
            StatusText.Text = Str.T("Str.AbAllTested");
    }

    private sealed record WizardSessionOption(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record WizardStepVm(
        string Step,
        string Title,
        string StatusText,
        string Level,
        string ResultText,
        string RunLabel,
        bool CanRun,
        List<WizardSessionOption> SessionOptions,
        WizardSessionOption? SelectedSession,
        bool CanEditSession);

    private void RebuildWizardUi()
    {
        var options = BuildSessionOptions();
        var steps = new List<WizardStepVm>();

        var baselineLevel = _wizard.BaselineDone ? (_wizard.BaselineStable ? "done" : "reverted") : "";
        if (_wizard.RunningStep == WizardSteps.Baseline)
            baselineLevel = "running";
        steps.Add(new WizardStepVm(
            WizardSteps.Baseline,
            "1 · 基线采样",
            _wizard.RunningStep == WizardSteps.Baseline
                ? Str.T("Str.Running")
                : _wizard.BaselineDone
                ? $"完成（平均 {_wizard.BaselineAvgFps:0.#} FPS，CV {_wizard.BaselineCv:0.###}{(_wizard.BaselineStable ? "" : "，不稳定")}）"
                : Str.T("Str.StatusToRun"),
            baselineLevel,
            _wizard.BaselineDone
                ? $"1% low {_wizard.BaselineP1Low:0.#} · {_wizard.BaselineAt:yy-MM-dd HH:mm}。基线稳定后才能测试候选组。"
                : Str.T("Str.AbBaselineHint"),
            _wizard.BaselineDone ? Str.T("Str.Resample") : Str.T("Str.StartSampling"),
            !_running && !_wizard.IsFutureSchema,
            options,
            FindSessionOption(options, _wizard.BaselineSessionId),
            !_running && !_wizard.IsFutureSchema));

        foreach (var group in WizardSteps.Groups)
        {
            var result = _wizard.GroupResult(group);
            var blocked = ExperimentWizard.CanRunStep(_wizard, group);
            var isRunning = _wizard.RunningStep == group;
            steps.Add(new WizardStepVm(
                group,
                $"{2 + Array.IndexOf(WizardSteps.Groups, group)} · {WizardSteps.DisplayName(group)}",
                isRunning
                    ? Str.T("Str.Running")
                    : result is null ? (blocked is null ? Str.T("Str.StatusToRun") : Str.T("Str.Locked")) : result.Keep == true ? "完成：保留" : result.Reverted ? "完成：已还原" : "完成：未还原",
                isRunning ? "running" : result is null ? "" : result.Keep == true ? "done" : result.Reverted ? "reverted" : "error",
                isRunning
                    ? "脚本正在执行，请保持游戏场景固定；可取消并在结果未知时检查后重试。"
                    : result is null
                    ? (blocked is not null ? blocked : Str.T("Str.AbCandidateHint"))
                    : $"{result.Reason}（平均 {result.AvgFps:0.#} FPS · 1% low {result.P1Low:0.#} · {result.CompletedAt:yy-MM-dd HH:mm}{(result.Simulated ? " · 模拟" : "")}）",
                isRunning ? Str.T("Str.Running") : Str.T("Str.Run"),
                !_running && !_wizard.IsFutureSchema && blocked is null,
                options,
                FindSessionOption(options, result?.SessionId),
                !_running && !_wizard.IsFutureSchema));
        }

        var reportBlocked = ExperimentWizard.CanRunStep(_wizard, WizardSteps.Report);
        var reportRunning = _wizard.RunningStep == WizardSteps.Report;
        steps.Add(new WizardStepVm(
            WizardSteps.Report,
            "5 · 生成报告",
            reportRunning ? Str.T("Str.Running") : _wizard.ReportGenerated ? $"已生成（{_wizard.ReportAt:yy-MM-dd HH:mm}）" : reportBlocked is null ? Str.T("Str.StatusToRun") : Str.T("Str.Locked"),
            reportRunning ? "running" : _wizard.ReportGenerated ? "done" : "",
            reportRunning
                ? "正在合成脚本原始指标与会话摘要；可取消并在结果未知时重试。"
                : reportBlocked is null
                    ? Str.T("Str.AbReportHint")
                    : reportBlocked,
            reportRunning ? Str.T("Str.Running") : Str.T("Str.GenerateReport"),
            !_running && reportBlocked is null,
            options,
            FindSessionOption(options, null),
            false));

        WizardStepsList.ItemsSource = steps;
    }

    private List<WizardSessionOption> BuildSessionOptions()
    {
        var options = new List<WizardSessionOption> { new(null, "（不关联会话）") };
        foreach (var s in PerformanceSessionStore.LoadAll().Take(15))
            options.Add(new WizardSessionOption(s.Id, $"{s.Name} · {s.StartedAt:MM-dd HH:mm} · {s.Samples.Count} 样本"));
        return options;
    }

    private static WizardSessionOption? FindSessionOption(List<WizardSessionOption> options, string? sessionId)
        => sessionId is null
            ? options.FirstOrDefault()
            : options.FirstOrDefault(o => o.Id == sessionId);

    // ---------- 步骤交互 ----------

    private void StepSession_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not WizardStepVm vm)
            return;
        if (_wizard.IsFutureSchema)
            return;
        // WizardStepVm 是不可变 record，不能依赖 SelectedItem 双向写回其 init 属性；
        // 直接读取控件当前选择，才能正确保存“取消关联”（null）。
        var sessionId = (combo.SelectedItem as WizardSessionOption)?.Id;
        if (vm.Step == WizardSteps.Baseline)
        {
            if (_wizard.BaselineSessionId == sessionId)
                return;
            _wizard = _wizard with { BaselineSessionId = sessionId };
        }
        else if (WizardSteps.IsGroup(vm.Step))
        {
            var result = _wizard.GroupResult(vm.Step);
            if (result is null || result.SessionId == sessionId)
                return;
            _wizard = ExperimentWizard.WithGroupResult(_wizard, result with { SessionId = sessionId }, sessionId);
        }
        else
        {
            return;
        }
        SaveWizardState();
        RebuildWizardUi();
    }

    private bool SaveWizardState()
    {
        if (_wizard.IsFutureSchema)
        {
            StatusText.Text = _wizard.CompatibilityError;
            return false;
        }
        if (ExperimentWizardStore.TrySave(_wizard, out var error))
            return true;
        StatusText.Text = "向导状态保存失败：" + error + "。本次结果未被可靠记录，请先解决权限/磁盘问题后重试。";
        return false;
    }

    private void CancelRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || _runCancellation is null)
            return;
        _cancelRequested = true;
        CancelRunButton.IsEnabled = false;
        StatusText.Text = "正在取消当前步骤……将停止脚本，当前结果不会形成结论。";
        _runCancellation.Cancel();
    }

    /// <summary>主窗口关闭时停止本页启动的脚本；不会按模糊进程名影响其他程序。</summary>
    public void CancelPendingRun()
    {
        if (_running)
            _runCancellation?.Cancel();
    }

    private async void WizardStepRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WizardStepVm vm })
            return;
        if (_running)
            return;

        var blocked = ExperimentWizard.CanRunStep(_wizard, vm.Step);
        if (blocked is not null)
        {
            StatusText.Text = "无法开始：" + blocked;
            return;
        }

        _running = true;
        _cancelRequested = false;
        using var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        CancelRunButton.IsEnabled = true;
        var sessionId = vm.SelectedSession?.Id;
        _wizard = ExperimentWizard.WithRunning(_wizard, vm.Step);
        if (!SaveWizardState())
        {
            _wizard = ExperimentWizard.WithError(_wizard, vm.Step, "向导状态无法保存，步骤尚未启动。请解决权限/磁盘问题后重试。");
            _running = false;
            _runCancellation = null;
            CancelRunButton.IsEnabled = false;
            RebuildWizardUi();
            return;
        }
        RebuildWizardUi();
        StatusText.Text = $"正在运行：{WizardSteps.DisplayName(vm.Step)}……运行期间步骤按钮已禁用，请保持游戏场景固定。";
        RawBox.Text = "正在运行： " + WizardSteps.DisplayName(vm.Step);
        ResetMetrics();

        try
        {
            var step = vm.Step switch
            {
                WizardSteps.Baseline => "baseline",
                WizardSteps.Report => "report",
                _ => vm.Step,
            };
            // 编排全程在本进程内（ExperimentRunner）：应用/还原直接调引擎，
            // 采样直接调 PresentMon，不再经过 PowerShell 脚本。
            var (exitCode, json) = await ExperimentRunner.RunAsync(
                step, new ExperimentRunner.Options(), cancellation.Token);

            RawBox.Text = exitCode == 0
                ? json
                : $"exit={exitCode}\n\n{json}";
            _wizard = ExperimentWizard.WithRawOutput(_wizard, RawBox.Text);

            ApplyStepResult(vm.Step, exitCode == 0, exitCode == 0 ? json : null, sessionId);
        }
        catch (OperationCanceledException)
        {
            RawBox.Text = "步骤已取消。脚本未完成，结果未知；如果候选组已经应用，请先检查/还原设置，再重试。";
            _wizard = ExperimentWizard.WithRawOutput(_wizard, RawBox.Text);
            ApplyStepResult(vm.Step, false, null, sessionId,
                "用户取消了步骤；脚本已停止，结果未知。请检查候选设置后重试。");
        }
        catch (Exception ex)
        {
            RawBox.Text = ex.ToString();
            _wizard = ExperimentWizard.WithRawOutput(_wizard, RawBox.Text);
            ApplyStepResult(vm.Step, false, null, sessionId, ex.Message);
        }
        finally
        {
            _runCancellation = null;
            CancelRunButton.IsEnabled = false;
            _running = false;
            _wizard = _wizard with { RunningStep = null, RunningSince = null };
            if (_cancelRequested && _wizard.LastError is null)
                _wizard = ExperimentWizard.WithError(_wizard, vm.Step, "用户取消了步骤；结果未知，可重试。");
            SaveWizardState();
            RebuildWizardUi();
            RefreshHistory();
            ShowStateMessage();
        }
    }

    /// <summary>解析脚本 JSON 输出并更新向导状态机。</summary>
    private void ApplyStepResult(string step, bool success, string? stdout, string? sessionId, string? exceptionMessage = null)
    {
        if (!success)
        {
            _wizard = ExperimentWizard.WithError(_wizard, step,
                $"步骤执行失败{(exceptionMessage is null ? "（脚本返回非零退出码）" : "：" + exceptionMessage)}");
            StatusText.Text = "执行失败，详见原始输出。可重试该步骤。";
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(stdout ?? "");
        }
        catch
        {
            _wizard = ExperimentWizard.WithError(_wizard, step, "脚本输出无法解析为 JSON（可能被其他输出污染）。");
            StatusText.Text = "输出解析失败，详见原始输出。";
            return;
        }
        if (root is not JsonObject obj)
        {
            _wizard = ExperimentWizard.WithError(_wizard, step, "脚本输出不是 JSON 对象。");
            StatusText.Text = "输出解析失败，详见原始输出。";
            return;
        }

        if (obj["ok"]?.GetValue<bool>() == false)
        {
            var error = obj["error"]?.GetValue<string>() ?? "脚本报告失败（未提供原因）";
            _wizard = ExperimentWizard.WithError(_wizard, step, error);
            StatusText.Text = "步骤未完成：" + error;
            return;
        }

        var mode = obj["mode"]?.GetValue<string>() ?? "";
        if (step == WizardSteps.Baseline && mode == "baseline")
        {
            var summary = obj["baseline"]?["summary"] as JsonObject;
            if (!TryReadRequiredMetric(summary, "avgFps", out var avg)
                || !TryReadRequiredMetric(summary, "p1Low", out var p1)
                || !TryReadRequiredMetric(summary, "cv", out var cv))
            {
                _wizard = ExperimentWizard.WithError(_wizard, step, "脚本基线结果缺少有效指标，未保存为完成状态。");
                StatusText.Text = "基线结果无效，详见原始输出。";
                return;
            }
            var stable = summary?["stable"]?.GetValue<bool>() ?? false;
            _wizard = ExperimentWizard.WithBaseline(_wizard, avg, p1, cv, stable, sessionId);
            SetMetrics(summary);
            DecisionText.Text = Str.T("Str.Baseline");
            StatusText.Text = obj["message"]?.GetValue<string>() ?? Str.T("Str.AbBaselineDone");
            TrayService.NotifyComplete(Str.T("Str.AppNameAb"), StatusText.Text);
        }
        else if (WizardSteps.IsGroup(step) && mode == "test")
        {
            var summary = obj["groupSummary"] as JsonObject;
            if (!TryReadRequiredMetric(summary, "avgFps", out var avg)
                || !TryReadRequiredMetric(summary, "p1Low", out var p1))
            {
                _wizard = ExperimentWizard.WithError(_wizard, step, "脚本候选组结果缺少有效指标，未保存为完成状态。");
                StatusText.Text = "候选组结果无效，详见原始输出。";
                return;
            }
            var keep = obj["keep"]?.GetValue<bool>() == true;
            var reverted = obj["reverted"]?.GetValue<bool>() ?? false;
            var reason = obj["reason"]?.GetValue<string>() ?? "";
            _wizard = ExperimentWizard.WithGroupResult(_wizard,
                new WizardGroupResult(step, keep, reverted, reason, avg, p1, sessionId,
                    obj["samplerMode"]?.GetValue<string>() == "simulated", DateTime.Now),
                sessionId);
            SetMetrics(summary);
            DecisionText.Text = keep ? "keep / 保留" : reverted ? "revert / 已还原" : "revert 未完成 / 未还原";
            StatusText.Text = obj["message"]?.GetValue<string>() ?? Str.T("Str.AbTestDone");
            TrayService.NotifyComplete(Str.T("Str.AppNameAb"), StatusText.Text);
        }
        else if (step == WizardSteps.Report && mode == "report")
        {
            if (obj["baseline"] is not JsonObject reportBaseline
                || !TryReadRequiredMetric(reportBaseline, "avgFps", out _)
                || !TryReadRequiredMetric(reportBaseline, "p1Low", out _)
                || obj["groups"] is not JsonArray reportGroups
                || !WizardSteps.Groups.All(id => reportGroups
                    .OfType<JsonObject>()
                    .Any(group => string.Equals(group["id"]?.GetValue<string>(), id, StringComparison.Ordinal))) )
            {
                _wizard = ExperimentWizard.WithError(_wizard, step, "脚本报告缺少完整基线或候选组指标，未标记为已生成。");
                StatusText.Text = "报告结果不完整，详见原始输出。";
                return;
            }
            if (!TryComposeReportFile(obj, out var reportError))
            {
                _wizard = ExperimentWizard.WithError(_wizard, step, reportError ?? "报告文件写入失败。");
                StatusText.Text = "报告写入失败，详见原始输出。可重试。";
                return;
            }
            _wizard = ExperimentWizard.WithReportGenerated(_wizard);
            SetMetrics(obj["baseline"] as JsonObject);
            DecisionText.Text = Str.T("Str.ReportGenerated");
            StatusText.Text = Str.T("Str.ReportGeneratedDetail");
        }
        else
        {
            _wizard = ExperimentWizard.WithError(_wizard, step, $"脚本返回模式（{mode}）与请求步骤（{step}）不匹配。");
            StatusText.Text = "结果与步骤不匹配，详见原始输出。";
        }
    }

    private static bool TryReadRequiredMetric(JsonObject? summary, string key, out double value)
    {
        value = 0;
        try
        {
            value = summary?[key]?.GetValue<double>() ?? double.NaN;
            return double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>报告 = 脚本原始指标 + 关联性能会话的摘要，写入 experiment/report-latest.md。</summary>
    private bool TryComposeReportFile(JsonObject scriptReport, out string? error)
    {
        error = null;
        var sb = new StringBuilder();
        sb.AppendLine("# FPS 帧律 · A/B 实验报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("- 结论口径：仅来自实际/模拟采样的统计比较；启发式数据不代表因果，不承诺固定 FPS 提升。");
        sb.AppendLine();

        var baseline = scriptReport["baseline"] as JsonObject;
        sb.AppendLine("## 基线");
        sb.AppendLine();
        if (baseline is not null)
            sb.AppendLine($"平均 {baseline["avgFps"]} FPS · 1% low {baseline["p1Low"]} · P99 {baseline["p99Ms"]} ms · 卡顿 {baseline["stutters"]} · CV {baseline["cv"]}");
        else
            sb.AppendLine("（无基线数据）");
        if (_wizard.BaselineSessionId is { } bsId
            && PerformanceSessionStore.LoadAll().FirstOrDefault(s => s.Id == bsId) is { } bsSession)
        {
            sb.AppendLine();
            sb.AppendLine($"关联性能会话：{bsSession.Name}（{bsSession.StartedAt:yyyy-MM-dd HH:mm}，{bsSession.Samples.Count} 样本）");
            sb.AppendLine(FormatSessionSummaryLine(bsSession));
        }

        sb.AppendLine();
        sb.AppendLine("## 候选组结论");
        sb.AppendLine();
        var groups = scriptReport["groups"] as JsonArray;
        if (groups is not null && groups.Count > 0)
        {
            foreach (var g in groups.OfType<JsonObject>())
            {
                var id = g["id"]?.GetValue<string>() ?? "";
                sb.AppendLine($"- **{g["name"]?.GetValue<string>() ?? id}（{id}）**：{(g["keep"]?.GetValue<bool>() == true ? Str.T("Str.Keep") : g["reverted"]?.GetValue<bool>() == true ? Str.T("Str.Restored") : Str.T("Str.NotRestored"))}"
                              + $" —— 平均 {g["summary"]?["avgFps"]} FPS、1% low {g["summary"]?["p1Low"]}。{g["reason"]?.GetValue<string>()}");
                if (_wizard.GroupResult(id)?.SessionId is { } sid
                    && PerformanceSessionStore.LoadAll().FirstOrDefault(s => s.Id == sid) is { } session)
                {
                    sb.AppendLine($"  - 关联性能会话：{session.Name}（{session.StartedAt:yyyy-MM-dd HH:mm}）");
                    sb.AppendLine("    - " + FormatSessionSummaryLine(session));
                }
            }
        }
        else
        {
            sb.AppendLine("（尚未测试任何候选组）");
        }

        sb.AppendLine();
        sb.AppendLine("## 脚本原始输出（JSON）");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(scriptReport.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune", "experiment");
            Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(Path.Combine(dir, "report-latest.md"), sb.ToString(), new UTF8Encoding(false));
            RawBox.Text = sb.ToString() + "\n\n（原始 JSON 输出见脚本；文件已保存 report-latest.md）";
            return true;
        }
        catch (Exception ex)
        {
            error = "报告文件写入失败：" + ex.Message;
            RawBox.Text = error + "\n\n" + sb;
            return false;
        }
    }

    private static string FormatSessionSummaryLine(PerformanceSession session)
    {
        var sum = SessionStatistics.Summarize(session);
        var parts = new List<string> { $"{sum.SampleCount} 样本" };
        if (sum.Cpu is { } c) parts.Add($"CPU 平均 {c.Avg}% / 95 位 {c.HighP95}%");
        if (sum.Mem is { } m) parts.Add($"内存平均 {m.Avg}%");
        if (sum.Gpu is { } g) parts.Add($"GPU 平均 {g.Avg}%");
        if (sum.VramAvgMib is { } v) parts.Add($"显存平均 {v:0} MiB");
        return Str.T("Str.SummaryLabel") + string.Join(" · ", parts) + "（启发式判断，不代表因果）";
    }

    // ---------- 指标瓦片 ----------

    private void ResetMetrics()
    {
        AvgFpsText.Text = "--";
        P1LowText.Text = "--";
        P99Text.Text = "--";
        StutterText.Text = "--";
        CvText.Text = "--";
        DecisionText.Text = "--";
    }

    private void SetMetrics(JsonObject? summary)
    {
        if (summary is null)
            return;

        if (TryGet(summary, "avgFps") is { } avg) AvgFpsText.Text = avg;
        if (TryGet(summary, "p1Low") is { } p1) P1LowText.Text = p1;
        if (TryGet(summary, "p99Ms") is { } p99) P99Text.Text = p99;
        if (TryGet(summary, "stutters") is { } st) StutterText.Text = st;
        if (TryGet(summary, "cv") is { } cv) CvText.Text = cv;
    }

    private static string? TryGet(JsonNode? node, string key)
        => node?[key]?.ToString();

    private void OpenDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "experiment");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", $"\"{dir}\"");
    }

    // ---------- 历史趋势图 ----------

    private void RefreshHistory()
    {
        var runs = ExperimentHistory.Load();
        if (runs.Count == 0)
        {
            HistoryCanvas.Children.Clear();
            HistoryEmptyText.Visibility = Visibility.Visible;
            return;
        }
        HistoryEmptyText.Visibility = Visibility.Collapsed;
        DrawChart(runs);
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistory();
    }

    /// <summary>
    /// 自绘折线图：横轴是实验记录（时间序），两条曲线分别是平均 FPS 与 1% low。
    /// 只用 WPF 基本图元，不引第三方图表库。
    /// </summary>
    private void DrawChart(List<ExperimentRun> runs)
    {
        const int maxPoints = 12;
        if (runs.Count > maxPoints)
            runs = runs.TakeLast(maxPoints).ToList();

        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var primary = (Brush)Application.Current.Resources["PrimaryBrush"];
        var warning = (Brush)Application.Current.Resources["WarningBrush"];
        var muted = (Brush)Application.Current.Resources["TextMutedBrush"];
        var border = (Brush)Application.Current.Resources["BorderBrush"];

        HistoryCanvas.Children.Clear();

        const double left = 46, right = 16, top = 10, bottom = 24;
        var w = Math.Max(HistoryCanvas.ActualWidth, 320);
        var h = Math.Max(HistoryCanvas.ActualHeight, 120);
        var plotW = w - left - right;
        var plotH = h - top - bottom;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var r in runs)
        {
            min = Math.Min(min, Math.Min(r.AvgFps, r.P1Low));
            max = Math.Max(max, Math.Max(r.AvgFps, r.P1Low));
        }
        if (min == max) { min -= 5; max += 5; }
        var pad = (max - min) * 0.15;
        min = Math.Max(0, min - pad);
        max += pad;

        double X(int i) => runs.Count == 1 ? left + plotW / 2 : left + plotW * i / (runs.Count - 1);
        double Y(double v) => top + plotH * (1 - (v - min) / (max - min));

        // 横向网格线 + 纵轴刻度
        for (var g = 0; g <= 4; g++)
        {
            var v = min + (max - min) * g / 4;
            var y = Y(v);
            HistoryCanvas.Children.Add(new Line
            {
                X1 = left, Y1 = y, X2 = left + plotW, Y2 = y,
                Stroke = border, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
                Opacity = 0.55
            });
            var label = new TextBlock
            {
                Text = Math.Round(v, 0).ToString("0"),
                FontSize = 10,
                Foreground = muted
            };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, y - 7);
            HistoryCanvas.Children.Add(label);
        }

        // 纵轴线
        HistoryCanvas.Children.Add(new Line
        {
            X1 = left, Y1 = top, X2 = left, Y2 = top + plotH,
            Stroke = border, StrokeThickness = 1
        });

        // 两条折线 + 数据点
        DrawSeries(runs, r => r.AvgFps, accent, warning, X, Y);
        DrawSeries(runs, r => r.P1Low, primary, warning, X, Y);

        // 横轴标签
        for (var i = 0; i < runs.Count; i++)
        {
            var lb = new TextBlock
            {
                Text = runs[i].ShortLabel,
                FontSize = 10,
                Foreground = muted
            };
            lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(lb, X(i) - lb.DesiredSize.Width / 2);
            Canvas.SetTop(lb, top + plotH + 5);
            HistoryCanvas.Children.Add(lb);
        }
    }

    private void DrawSeries(
        List<ExperimentRun> runs, Func<ExperimentRun, double> pick,
        Brush stroke, Brush revertStroke,
        Func<int, double> x, Func<double, double> y)
    {
        var poly = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        for (var i = 0; i < runs.Count; i++)
            poly.Points.Add(new Point(x(i), y(pick(runs[i]))));
        HistoryCanvas.Children.Add(poly);

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var reverted = run.Keep == false;
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = stroke,
                Stroke = reverted ? revertStroke : Brushes.Transparent,
                StrokeThickness = reverted ? 2 : 0,
                ToolTip = BuildTooltip(run)
            };
            Canvas.SetLeft(dot, x(i) - 4);
            Canvas.SetTop(dot, y(pick(run)) - 4);
            HistoryCanvas.Children.Add(dot);
        }
    }

    private static string BuildTooltip(ExperimentRun r)
    {
        var time = r.Time == DateTime.MinValue ? "" : $"\n{r.Time:yyyy-MM-dd HH:mm}";
        var verdict = r.Keep is null
            ? ""
            : $"\n结论：{(r.Keep == true ? Str.T("Str.Keep") : r.Reverted == true ? Str.T("Str.Restored") : Str.T("Str.NotRestored"))}";
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? "" : $"\n{r.Reason}";
        return $"{r.Name}（{r.Id}）{time}\n平均 {r.AvgFps} FPS · 1% low {r.P1Low} FPS{verdict}{reason}";
    }

    // 原始输出框自身可滚: 滚到尽头时把滚轮还给页面根滚动(与检测页同款)
    private void RawBoxWheelToRoot(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        var atTop = RawBox.VerticalOffset <= 0.1;
        var atBottom = RawBox.VerticalOffset >= RawBox.ExtentHeight - RawBox.ViewportHeight - 0.1;
        if ((e.Delta < 0 && !atBottom) || (e.Delta > 0 && !atTop))
            return; // 框内还有内容可滚
        RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
