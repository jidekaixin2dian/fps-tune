using System.IO;
using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 性能会话页：开始/停止本地采样、实时四指标曲线、启发式洞察、
/// 历史会话管理与导出、两次会话并排比较。
/// 会话采样服务归 App 所有，页面切换不中断运行中的会话。
/// </summary>
public partial class SessionView : UserControl
{
    private readonly DispatcherTimer _ticker;
    private bool _refreshing;
    private int _historyGeneration;
    private string? _preparationHint;
    private IReadOnlyList<PerformanceSession> _sessions = Array.Empty<PerformanceSession>();
    private IReadOnlyDictionary<string, SessionSummary> _summaries = new Dictionary<string, SessionSummary>();

    private readonly PerformanceSessionService _service;
    private readonly AutoRecordingService? _automation;
    public SessionView() : this(App.SessionService, App.AutomaticRecording) { }
    internal SessionView(PerformanceSessionService service, AutoRecordingService? automation = null)
    {
        _service = service; _automation = automation;
        InitializeComponent();
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => UpdateRunState();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private PerformanceSessionService Service => _service;

    internal void PrepareDeltaSession()
    {
        if (Service.IsRunning) return;
        if (string.IsNullOrWhiteSpace(SessionNameBox.Text))
        {
            var game = !string.IsNullOrWhiteSpace(AppState.GamePath)
                ? GamePathService.LabelFor(AppState.GamePath)
                : Str.T("Str.Comparison");
            SessionNameBox.Text = Str.T("Str.SessionDefaultName", game, DateTime.Now);
        }
        _preparationHint = Str.T("Str.RecordMatchHint");
        UpdateRunState();
        SessionNameBox.Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Service.Sampled += Service_Sampled;
        Service.StateChanged += Service_StateChanged;
        Service.AutomaticStopFailed += Service_AutomaticStopFailed;
        GameContextService.GameSwitched += UpdateConditionGame;
        if (_automation is not null) _automation.Changed += UpdateAutoRecordStatus;
        InitializeAutoRecording();
        UpdateConditionGame();
        if (Service.IsRunning) FillConditions(Service.Conditions);
        RefreshAll();
        if (Service.IsRunning)
            _ticker.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Service.Sampled -= Service_Sampled;
        Service.StateChanged -= Service_StateChanged;
        Service.AutomaticStopFailed -= Service_AutomaticStopFailed;
        GameContextService.GameSwitched -= UpdateConditionGame;
        if (_automation is not null) _automation.Changed -= UpdateAutoRecordStatus;
        _ticker.Stop();
        _historyGeneration++;
        _refreshing = false;
        _sessions = Array.Empty<PerformanceSession>();
        _summaries = new Dictionary<string, SessionSummary>();
        _visibleSessions = []; _historyRows = [];
        HistoryList.ItemsSource = CompareA.ItemsSource = CompareB.ItemsSource = null;
        CompareRows.ItemsSource = InsightList.ItemsSource = null;
    }

    // ---------- 状态与实时指标 ----------

    private void Service_StateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Service_StateChanged);
            return;
        }
        RefreshControlState();
        if (Service.IsRunning)
        {
            FillConditions(Service.Conditions);
            _ticker.Start();
        }
        else
        {
            _ticker.Stop();
            RefreshAll();
        }
    }

    private void Service_AutomaticStopFailed(string message)
        => Dispatcher.BeginInvoke(() => { _preparationHint = Str.T("Str.ConditionStopFailed", message); UpdateRunState(); });

    private void UpdateConditionGame()
    {
        var name = Service.IsRunning ? Service.Conditions?.GameName :
            string.IsNullOrWhiteSpace(AppState.GamePath) ? null : GameContextService.DisplayNameFor(AppState.GamePath);
        ConditionGameText.Text = Str.T("Str.ConditionGameValue", name ?? Str.T("Str.ConditionsUnknown"));
        UpdateTemplateState();
    }

    private void Service_Sampled(MetricSample sample)
    {
        if (!IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Service_Sampled(sample));
            return;
        }
        UpdateTiles(sample);
        DrawCharts();
    }

    private void UpdateRunState()
    {
        if (!IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized) return;
        if (Service.IsStopping)
            RunStateText.Text = Str.T("Str.SessionSaving");
        else if (Service.IsRunning)
        {
            var unavailable = Service.CurrentUnavailableReasons;
            var missing = unavailable.Count == 0 ? Str.T("Str.None") : string.Join(Str.T("Str.ListSeparatorSemicolon"), unavailable.Keys);
            RunStateText.Text = Str.T("Str.SessionRunningLine", Service.SessionName, FormatDuration(Service.Elapsed),
                Service.RunningBuffer.Count, Service.SampleIntervalSeconds, missing);
            if (Service.CurrentFpsUnavailableReasonKey is { } fpsKey)
                RunStateText.Text += " · " + Str.T("Str.FpsUnavailableShort", Str.T(fpsKey));
        }
        else
        {
            RunStateText.Text = _preparationHint ?? Str.T("Str.SessionReady");
        }
    }

    private void RefreshControlState()
    {
        var running = Service.IsRunning;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running && !Service.IsStopping;
        CancelButton.IsEnabled = running && !Service.IsStopping;
        SessionNameBox.IsEnabled = !running;
        ConditionsPanel.IsEnabled = !running;
        UpdateConditionGame();
        UpdateRunState();
        if (running)
            SessionNameBox.Text = Service.SessionName;
    }

    private void UpdateTiles(MetricSample? sample)
    {
        sample ??= Service.LatestSample;
        if (sample is null)
            return;
        CpuNowText.Text = sample.CpuPercent is { } c ? $"{c:0}%" : Str.T("Str.Unavailable");
        MemNowText.Text = sample.MemoryPercent is { } m ? $"{m:0}%" : Str.T("Str.Unavailable");
        GpuNowText.Text = sample.GpuPercent is { } g ? $"{g:0}%" : Str.T("Str.Unavailable");

        if (sample.VramUsedBytes is { } used)
        {
            var total = sample.VramTotalBytes;
            VramNowText.Text = total is { } t && t > 0
                ? $"{used / t * 100.0:0}%"
                : $"{used / 1024.0 / 1024.0:0} MiB";
            VramTotalText.Text = total is { } tt && tt > 0
                ? Str.T("Str.SessionVramCapacity", used / 1024.0 / 1024.0, tt / 1024.0 / 1024.0)
                : Str.T("Str.SessionVramCapacityUnknown");
        }
        else
        {
            VramNowText.Text = Str.T("Str.Unavailable");
            VramTotalText.Text = Service.CurrentUnavailableReasons.TryGetValue("vram", out var reason)
                ? reason
                : "";
        }
    }

    private void DrawCharts()
    {
        var buffer = Service.RunningBuffer;
        if (buffer.Count == 0)
            return;
        var tail = buffer.Count > 60 ? buffer.TakeLast(60).ToArray() : buffer.ToArray();
        MiniChart.Draw(CpuChart, tail.Select(s => s.CpuPercent ?? double.NaN).ToList(), "AccentBrush");
        MiniChart.Draw(MemChart, tail.Select(s => s.MemoryPercent ?? double.NaN).ToList(), "ChartSecondaryBrush");
        MiniChart.Draw(GpuChart, tail.Select(s => s.GpuPercent ?? double.NaN).ToList(), "OkBrush");

        // 显存：容量可得时画占比，否则画窗口内相对水位
        if (tail[0].VramTotalBytes is { } total && total > 0)
            MiniChart.Draw(VramChart, tail.Select(s => s.VramUsedBytes is { } u ? u / total * 100.0 : double.NaN).ToList(), "WarningBrush");
        else
        {
            var peak = tail.Where(s => s.VramUsedBytes is not null).Select(s => s.VramUsedBytes ?? 0).DefaultIfEmpty(0).Max();
            MiniChart.Draw(VramChart, tail.Select(s => s.VramUsedBytes is { } u && peak > 0 ? u / peak * 100.0 : double.NaN).ToList(), "WarningBrush");
        }
    }

    // ---------- 开始 / 停止 / 取消 ----------

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Service.IsRunning)
            return;
        var name = SessionNameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
            name = PerformanceSessionService.DefaultSessionName();
        if (!PerformanceSessionService.IsValidSessionName(name))
        {
            DialogService.Warning(Str.T("Str.NavSession"), Str.T("Str.SessionNameInvalid"));
            return;
        }
        if (!TryReadConditions(out var conditions))
        {
            RunStateText.Text = Str.T("Str.ConditionInvalid");
            return;
        }
        _preparationHint = null;
        try
        {
            if (!Service.Start(name, conditions) && !TryArchiveUnreadableSession(name, conditions))
            {
                DialogService.Warning(
                    Str.T("Str.NavSession"),
                    Str.T("Str.SessionStartBusy", Path.Combine(PerformanceSessionStore.SessionsDir, "_active.json")));
                RefreshControlState();
                return;
            }
        }
        catch (Exception ex)
        {
            // Start 在开局准备运行中、条件非法等情况下会抛 InvalidOperationException/ArgumentException：
            // 写状态行而不是全局错误框，用户看得到原因也能直接重试。
            RunStateText.Text = Str.T("Str.SessionStartFailed", PrivacyScrub.Sanitize(ex.Message));
            RefreshControlState();
            return;
        }
        UpdateTiles(null);
        DrawCharts();
    }

    /// <summary>
    /// 上次的活动快照本版本解释不了（更新版本写入或已损坏）时，让用户明确选择Str.T("Str.ArchiveRename")再开始：
    /// 既不丢数据，也不会把会话功能永久锁死。
    /// </summary>
    private bool TryArchiveUnreadableSession(string name, SessionConditions? conditions)
    {
        if (Service.IsRunning || !PerformanceSessionStore.HasUnreadableActiveSnapshot())
            return false;

        var go = DialogService.Confirm(
            Str.T("Str.NavSession"),
            Str.T("Str.SessionUnreadableConfirm"),
            confirmText: Str.T("Str.ArchiveAndStart"));
        if (!go)
            return false;

        if (!PerformanceSessionStore.TryArchiveUnreadableActiveSnapshot(out var archived, out var error))
        {
            DialogService.Warning(Str.T("Str.NavSession"), Str.T("Str.SessionArchiveFailed", error));
            return false;
        }

        DialogService.Info(
            Str.T("Str.NavSession"),
            Str.T("Str.SessionArchived", Path.GetFileName(archived)));
        return Service.Start(name, conditions);
    }

    internal bool TryReadConditions(out SessionConditions conditions)
    {
        var valid = Number(ResolutionWidthBox.Text, out var w) & Number(ResolutionHeightBox.Text, out var h)
            & Number(TargetSecondsBox.Text, out var seconds);
        conditions = new SessionConditions(Width: w, Height: h, Quality: QualityBox.Text.Trim(),
            Scene: SceneBox.Text.Trim(), TargetSeconds: seconds);
        return valid && SessionConditionRules.IsValid(conditions);
        static bool Number(string input, out int? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(input)) return true;
            if (!int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
            value = parsed; return true;
        }
    }

    private void FillConditions(SessionConditions? c)
    {
        ResolutionWidthBox.Text = c?.Width?.ToString(CultureInfo.InvariantCulture) ?? "";
        ResolutionHeightBox.Text = c?.Height?.ToString(CultureInfo.InvariantCulture) ?? "";
        QualityBox.Text = c?.Quality ?? "";
        SceneBox.Text = c?.Scene ?? "";
        TargetSecondsBox.Text = c?.TargetSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private void ReuseConditions_Click(object sender, RoutedEventArgs e)
    {
        if (Service.IsRunning) return;
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().ToArray();
        var previous = selected.Length == 1 ? _sessions.FirstOrDefault(s => s.Id == selected[0].Id) : null;
        if (previous?.Conditions is null)
        {
            RunStateText.Text = Str.T("Str.ConditionSelectPrevious"); return;
        }
        FillConditions(previous.Conditions);
        RunStateText.Text = Str.T("Str.ConditionCopied");
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!Service.IsRunning)
            return;
        try
        {
            var session = await Service.StopAsync(save: true);
            RefreshAll();
            if (session is not null) ShowInsights(session);
        }
        catch (Exception ex)
        {
            RefreshAll();
            DialogService.Warning(Str.T("Str.SessionSaveFailed"), Str.T("Str.SessionSaveFailedHint") + "\n\n" + ex.Message);
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!Service.IsRunning)
            return;
        if (!DialogService.Confirm(Str.T("Str.CancelSession"), Str.T("Str.ConfirmCancelSession"), danger: true, confirmText: Str.T("Str.CancelSession")))
            return;
        try
        {
            await Service.StopAsync(save: false);
            RefreshAll();
        }
        catch (Exception ex)
        {
            RefreshAll();
            DialogService.Warning(
                Str.T("Str.CancelFailed"),
                Str.T("Str.SessionCancelMarkFailed", ex.Message));
        }
    }

    // ---------- 历史 / 导出 / 删除 ----------

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private async void RefreshAll()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        var generation = ++_historyGeneration;
        try
        {
            RefreshControlState();
            var snapshot = await Task.Run(() =>
            {
                var history = PerformanceSessionStore.LoadHistory();
                var sessions = history.Select(s => s.Header).ToList();
                var summaries = history.ToDictionary(s => s.Header.Id, s => s.Summary);
                return (Sessions: sessions, Summaries: summaries, Rows: BuildHistoryRows(sessions, summaries));
            });
            if (generation != _historyGeneration || !IsLoaded) return;
            _sessions = snapshot.Sessions;
            _summaries = snapshot.Summaries;
            _historyRows = snapshot.Rows;
            RebuildHistoryGames();
            ApplyHistoryFilters();
            UpdateTiles(null);
            if (Service.IsRunning)
                DrawCharts();
        }
        catch (Exception ex)
        {
            if (generation == _historyGeneration && IsLoaded)
                RunStateText.Text = Str.T("Str.SessionHistoryLoadFailed", ex.Message);
        }
        finally
        {
            if (generation == _historyGeneration) _refreshing = false;
        }
    }

    private static List<SessionListVm> BuildHistoryRows(IReadOnlyList<PerformanceSession> sessions,
        IReadOnlyDictionary<string, SessionSummary> summaries)
    {
        return sessions.Select(s =>
        {
            var sum = summaries[s.Id];
            var avgParts = new List<string>();
            if (sum.Cpu is { } c) avgParts.Add($"CPU {c.Avg}%");
            if (sum.Mem is { } m) avgParts.Add(Str.T("Str.SessionMemAvg", m.Avg));
            if (sum.Gpu is { } g) avgParts.Add($"GPU {g.Avg}%");
            if (sum.VramAvgMib is { } v) avgParts.Add(Str.T("Str.SessionVramAvg", v));
            if (s.Fps is { } fps) avgParts.Add($"FPS {fps.AvgFps:0}");
            return new SessionListVm(
                s.Id,
                s.Name,
                Str.T("Str.ConditionHistoryMeta", s.StartedAt.ToString("yyyy-MM-dd HH:mm"), FormatDuration(sum.Duration), sum.SampleCount, SessionConditionRules.Describe(s.Conditions)),
                avgParts.Count > 0 ? string.Join(" · ", avgParts) : Str.T("Str.NotEnoughSamples"),
                s.StartedAt);
        }).ToList();
    }

    private sealed record SessionListVm(string Id, string Name, string MetaText, string AvgText, DateTime StartedAt);

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().ToList();
        ExportJsonButton.IsEnabled = selected.Count == 1;
        ExportCsvButton.IsEnabled = selected.Count == 1;
        DeleteButton.IsEnabled = selected.Count > 0;
        CompareSelectedButton.IsEnabled = selected.Count == 2;
        if (selected.Count == 1)
        {
            var store = _sessions.FirstOrDefault(s => s.Id == selected[0].Id);
            if (store is not null)
                ShowInsights(store);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var session = GetSingleSelectedSession();
        if (session is null)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Str.T("Str.ExportSessionJson"),
            Filter = "JSON (*.json)|*.json",
            FileName = Str.T("Str.SessionExportFilePrefix") + SanitizeFileName(session.Name)
                + "-" + session.StartedAt.ToString("yyyyMMdd-HHmmss") + ".json"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            SessionExporter.ExportJson(session, dlg.FileName);
            DialogService.Info(Str.T("Str.ExportDone"), Str.T("Str.SessionJsonSaved"));
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.ExportFailed"), Str.T("Str.SessionJsonExportFailed", ex.Message));
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var session = GetSingleSelectedSession();
        if (session is null)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Str.T("Str.ExportSessionCsv"),
            Filter = "CSV (*.csv)|*.csv",
            FileName = Str.T("Str.SessionExportFilePrefix") + SanitizeFileName(session.Name)
                + "-" + session.StartedAt.ToString("yyyyMMdd-HHmmss") + ".csv"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            SessionExporter.ExportCsv(session, dlg.FileName);
            DialogService.Info(Str.T("Str.ExportDone"), Str.T("Str.SessionCsvSaved"));
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.ExportFailed"), Str.T("Str.SessionCsvExportFailed", ex.Message));
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().ToList();
        if (selected.Count == 0)
            return;
        if (!DialogService.Confirm(
                Str.T("Str.DeleteSession"),
                Str.T("Str.SessionDeleteConfirm", selected.Count),
                danger: true, confirmText: Str.T("Str.Delete")))
            return;
        var failed = new List<string>();
        foreach (var vm in selected)
        {
            try
            {
                PerformanceSessionStore.Delete(vm.Id);
            }
            catch (Exception ex)
            {
                failed.Add($"{vm.Name} ({ex.Message})");
            }
        }
        RefreshAll();
        if (failed.Count > 0)
            DialogService.Warning(
                Str.T("Str.DeleteSession"),
                Str.T("Str.SessionDeleteFailed") + string.Join("; ", failed));
    }

    private PerformanceSession? GetSingleSelectedSession()
    {
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().ToList();
        if (selected.Count != 1)
        {
            DialogService.Info(Str.T("Str.NavSession"), Str.T("Str.SelectExactlyOneSession"));
            return null;
        }
        var path = Path.Combine(PerformanceSessionStore.SessionsDir, PerformanceSessionStore.FileNameFor(selected[0].Id));
        if (PerformanceSessionStore.TryLoadFile(path, out var session, out var error)) return session;
        DialogService.Warning(Str.T("Str.ExportFailed"), error ?? Str.T("Str.Unavailable"));
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------- 洞察 ----------

    private void ShowInsights(PerformanceSession session)
    {
        var summary = session.Samples.Count == 0 && _summaries.TryGetValue(session.Id, out var cached)
            ? cached : SessionStatistics.Summarize(session);
        var end = session.EndedAt
                  ?? (session.Samples.Count > 0 ? session.Samples[^1].T : session.StartedAt);
        var findings = SessionInsights.Evaluate(summary, session.StartedAt, end).ToList();

        // 0.2.0 C-E：FPS 结论置顶（有数报数，没数明示原因，不静默）。
        if (session.Fps is { } fps)
        {
            findings.Insert(0, new InsightFinding(
                "Fps", "info",
                Str.T("Str.SessionFps"),
                Str.T("Str.FpsDetail", fps.AvgFps, fps.P1Low, fps.P99Ms, fps.Stutters, fps.Samples),
                null, null, Array.Empty<string>()));
        }
        else if (session.FpsNote is { } noteKey)
        {
            findings.Insert(0, new InsightFinding(
                "FpsNote", "attention",
                Str.T("Str.SessionFps"),
                Str.T("Str.FpsUnavailableShort", Str.T(noteKey)),
                null, null, Array.Empty<string>()));
        }

        // 0.2.0 C-A：瓶颈判定（启发式阈值，文案声明不代表因果）。
        if (SessionInsights.ClassifyBottleneck(summary) is { } bn)
        {
            var (level, detail) = bn.Kind switch
            {
                "gpu" => ("attention", Str.T("Str.BottleneckGpu", bn.GpuAvg, bn.GpuHighP95)),
                "cpu" => ("attention", Str.T("Str.BottleneckCpu", bn.GpuAvg, bn.CpuHighP95)),
                _ => ("info", Str.T("Str.BottleneckBalanced", bn.GpuAvg, bn.CpuHighP95)),
            };
            findings.Insert(0, new InsightFinding(
                "Bottleneck", level, Str.T("Str.BottleneckTitle"), detail, null, null, Array.Empty<string>()));
        }

        InsightTargetText.Text = Str.T("Str.SessionInsightTarget", session.Name, session.StartedAt, summary.SampleCount);
        InsightList.ItemsSource = findings.Select(f => new InsightVm(
            f.Kind,
            f.Level,
            f.Title,
            f.Detail,
            (f.WindowStart is { } ws && f.WindowEnd is { } we
                ? Str.T("Str.SessionInsightWindow", ws, we)
                : "") +
            (f.MissingSignals.Count > 0
                ? (f.WindowStart is { } ? " · " : "") + Str.T("Str.MissingSignal") + string.Join(Str.T("Str.ListSeparator"), f.MissingSignals)
                : ""),
            KindLabel(f.Kind))).ToList();
    }

    private static string KindLabel(string kind) => kind switch
    {
        "CpuPressure" or "CpuLoad" => "CPU",
        "GpuSaturated" => "GPU",
        "Fps" or "FpsNote" => Str.T("Str.SessionFps"),
        "Bottleneck" => Str.T("Str.BottleneckTitle"),
        "VramPressure" or "VramLoad" or "VramUnknownTotal" => Str.T("Str.Vram"),
        "MemoryPressure" => Str.T("Str.Memory"),
        "MissingSignal" => Str.T("Str.SignalMissing"),
        _ => Str.T("Str.Verdict")
    };

    private sealed record InsightVm(
        string Kind, string Level, string Title, string Detail, string WindowText, string KindLabel);

    // ---------- 比较 ----------

    private void RefreshCompareSources()
    {
        var a = (CompareA.SelectedItem as CompareSourceVm)?.Id;
        var b = (CompareB.SelectedItem as CompareSourceVm)?.Id;
        var items = _visibleSessions.Select(s => new CompareSourceVm(s.Id, s.Name, s.StartedAt)).ToList();
        CompareA.ItemsSource = items;
        CompareB.ItemsSource = items;
        CompareA.SelectedItem = items.FirstOrDefault(s => s.Id == a);
        CompareB.SelectedItem = items.FirstOrDefault(s => s.Id == b);
    }

    private sealed record CompareSourceVm(string Id, string Name, DateTime StartedAt)
    {
        public string Display => Str.T("Str.SessionDisplayName", Name, StartedAt);
    }

    private void Compare_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildCompare();

    private void RebuildCompare()
    {
        _comparison = null;
        ComparisonExportStatus.Text = "";
        CompareHighlights.ItemsSource = null;
        SwapComparisonButton.IsEnabled = ExportComparisonButton.IsEnabled = false;
        if (CompareA.SelectedItem is not CompareSourceVm va || CompareB.SelectedItem is not CompareSourceVm vb)
        {
            CompareConditionsText.Visibility = Visibility.Collapsed;
            CompareHint.Text = Str.T("Str.SelectTwoSessions");
            CompareRows.ItemsSource = null;
            return;
        }
        var store = _sessions;
        var a = store.FirstOrDefault(s => s.Id == va.Id);
        var b = store.FirstOrDefault(s => s.Id == vb.Id);
        if (a is null || b is null)
            return;
        if (a.Id == b.Id)
        {
            CompareConditionsText.Visibility = Visibility.Collapsed;
            CompareHint.Text = Str.T("Str.SameSessionCannotCompare");
            CompareRows.ItemsSource = null;
            return;
        }

        var sa = _summaries[a.Id];
        var sb = _summaries[b.Id];
        CompareHint.Text = Str.T("Str.ConditionCompareMeta", a.Name, a.StartedAt.ToString("MM-dd HH:mm"), sa.SampleCount,
            b.Name, b.StartedAt.ToString("MM-dd HH:mm"), sb.SampleCount);
        var differences = SessionConditionRules.Differences(a, b);
        CompareConditionsText.Visibility = Visibility.Visible;
        CompareConditionsText.Text = differences.Count == 0 ? Str.T("Str.ConditionMatch") :
            Str.T("Str.ConditionWarning", string.Join("; ", differences));
        CompareConditionsText.SetResourceReference(TextBlock.ForegroundProperty, differences.Count == 0 ? "TextMutedBrush" : "WarningBrush");

        _comparison = SessionComparison.Create(a, b, sa, sb);
        CompareRows.ItemsSource = _comparison.Rows;
        CompareHighlights.ItemsSource = _comparison.Highlights;
        SwapComparisonButton.IsEnabled = ExportComparisonButton.IsEnabled = true;
    }

    private static string FormatDuration(TimeSpan d)
        => d.TotalHours >= 1 ? Str.T("Str.ConditionDurationHours", (int)d.TotalHours, d.Minutes)
            : Str.T("Str.ConditionDurationMinutes", (int)d.TotalMinutes, d.Seconds);
}
