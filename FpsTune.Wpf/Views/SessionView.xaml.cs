using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

    public SessionView()
    {
        InitializeComponent();
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => UpdateRunState();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private PerformanceSessionService Service => App.SessionService;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Service.Sampled += Service_Sampled;
        Service.StateChanged += Service_StateChanged;
        RefreshAll();
        if (Service.IsRunning)
            _ticker.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Service.Sampled -= Service_Sampled;
        Service.StateChanged -= Service_StateChanged;
        _ticker.Stop();
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
            _ticker.Start();
        else
            _ticker.Stop();
    }

    private void Service_Sampled(MetricSample sample)
    {
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
        if (Service.IsRunning)
        {
            var unavailable = Service.CurrentUnavailableReasons;
            var missing = unavailable.Count == 0 ? "无" : string.Join("；", unavailable.Keys);
            RunStateText.Text = $"采样中：{Service.SessionName} · 已运行 {FormatDuration(Service.Elapsed)} · " +
                                $"样本 {Service.RunningBuffer.Count} · 间隔 {PerformanceSessionService.CurrentInterval.TotalSeconds:0.#} 秒 · 缺失指标：{missing}";
        }
        else
        {
            RunStateText.Text = "就绪。输入名称后点击开始；运行中可随时停止并保存，或取消丢弃。";
        }
    }

    private void RefreshControlState()
    {
        var running = Service.IsRunning;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        CancelButton.IsEnabled = running;
        SessionNameBox.IsEnabled = !running;
        UpdateRunState();
        if (running)
            SessionNameBox.Text = Service.SessionName;
    }

    private void UpdateTiles(MetricSample? sample)
    {
        sample ??= Service.LatestSample;
        if (sample is null)
            return;
        CpuNowText.Text = sample.CpuPercent is { } c ? $"{c:0}%" : "不可用";
        MemNowText.Text = sample.MemoryPercent is { } m ? $"{m:0}%" : "不可用";
        GpuNowText.Text = sample.GpuPercent is { } g ? $"{g:0}%" : "不可用";

        if (sample.VramUsedBytes is { } used)
        {
            var total = sample.VramTotalBytes;
            VramNowText.Text = total is { } t && t > 0
                ? $"{used / t * 100.0:0}%"
                : $"{used / 1024.0 / 1024.0:0} MiB";
            VramTotalText.Text = total is { } tt && tt > 0
                ? $"{used / 1024.0 / 1024.0:0} / {tt / 1024.0 / 1024.0:0} MiB（注册表报告容量）"
                : "显存容量不可用，无法计算占比";
        }
        else
        {
            VramNowText.Text = "不可用";
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
        MiniChart.Draw(MemChart, tail.Select(s => s.MemoryPercent ?? double.NaN).ToList(), "PrimaryBrush");
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
            DialogService.Warning("性能会话", "会话名称包含不能用于文件的字符，请修改后重试。");
            return;
        }
        Service.Start(name);
        UpdateTiles(null);
        DrawCharts();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!Service.IsRunning)
            return;
        var session = Service.Stop(save: true);
        RefreshAll();
        if (session is not null)
            ShowInsights(session);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!Service.IsRunning)
            return;
        if (!DialogService.Confirm("取消会话", "确定取消当前会话？已采集的数据将被丢弃。", danger: true, confirmText: "取消会话"))
            return;
        Service.Cancel();
        RefreshAll();
    }

    // ---------- 历史 / 导出 / 删除 ----------

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            RefreshControlState();
            RefreshHistory();
            RefreshCompareSources();
            UpdateTiles(null);
            if (Service.IsRunning)
                DrawCharts();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshHistory()
    {
        var sessions = PerformanceSessionStore.LoadAll();
        HistoryList.ItemsSource = sessions.Select(s =>
        {
            var sum = SessionStatistics.Summarize(s);
            var avgParts = new List<string>();
            if (sum.Cpu is { } c) avgParts.Add($"CPU {c.Avg}%");
            if (sum.Mem is { } m) avgParts.Add($"内存 {m.Avg}%");
            if (sum.Gpu is { } g) avgParts.Add($"GPU {g.Avg}%");
            if (sum.VramAvgMib is { } v) avgParts.Add($"显存 {v:0} MiB");
            return new SessionListVm(
                s.Id,
                s.Name,
                $"{s.StartedAt:yyyy-MM-dd HH:mm} · {FormatDuration(sum.Duration)} · {sum.SampleCount} 样本",
                avgParts.Count > 0 ? string.Join(" · ", avgParts) : "有效样本不足",
                s.StartedAt);
        }).ToList();
        HistoryEmptyText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record SessionListVm(string Id, string Name, string MetaText, string AvgText, DateTime StartedAt);

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().ToList();
        ExportJsonButton.IsEnabled = selected.Count == 1;
        ExportCsvButton.IsEnabled = selected.Count == 1;
        DeleteButton.IsEnabled = selected.Count > 0;
        if (selected.Count == 1)
        {
            var store = PerformanceSessionStore.LoadAll().FirstOrDefault(s => s.Id == selected[0].Id);
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
            Title = "导出会话 JSON",
            Filter = "JSON (*.json)|*.json",
            FileName = $"FpsTune-会话-{SanitizeFileName(session.Name)}-{session.StartedAt:yyyyMMdd-HHmmss}.json"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            SessionExporter.ExportJson(session, dlg.FileName);
            DialogService.Info("导出完成", "JSON 已保存到所选位置。文件内容不含路径与用户名。");
        }
        catch (Exception ex)
        {
            DialogService.Warning("导出失败", "JSON 导出失败：" + ex.Message);
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var session = GetSingleSelectedSession();
        if (session is null)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出会话 CSV",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"FpsTune-会话-{SanitizeFileName(session.Name)}-{session.StartedAt:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            SessionExporter.ExportCsv(session, dlg.FileName);
            DialogService.Info("导出完成", "CSV 已保存到所选位置。缺失样本为空单元格。");
        }
        catch (Exception ex)
        {
            DialogService.Warning("导出失败", "CSV 导出失败：" + ex.Message);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().ToList();
        if (selected.Count == 0)
            return;
        if (!DialogService.Confirm(
                "删除会话",
                $"确定删除所选 {selected.Count} 个会话？此操作只删除这些会话文件，不影响其他数据。",
                danger: true, confirmText: "删除"))
            return;
        foreach (var vm in selected)
            PerformanceSessionStore.Delete(vm.Id);
        RefreshAll();
    }

    private PerformanceSession? GetSingleSelectedSession()
    {
        var selected = HistoryList.SelectedItems.OfType<SessionListVm>().ToList();
        if (selected.Count != 1)
        {
            DialogService.Info("性能会话", "请先在历史列表中选择恰好一个会话。");
            return null;
        }
        return PerformanceSessionStore.LoadAll().FirstOrDefault(s => s.Id == selected[0].Id);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------- 洞察 ----------

    private void ShowInsights(PerformanceSession session)
    {
        var summary = SessionStatistics.Summarize(session);
        var end = session.EndedAt
                  ?? (session.Samples.Count > 0 ? session.Samples[^1].T : session.StartedAt);
        var findings = SessionInsights.Evaluate(summary, session.StartedAt, end);
        InsightTargetText.Text = $"依据会话「{session.Name}」（{session.StartedAt:MM-dd HH:mm} 起，{summary.SampleCount} 个样本）";
        InsightList.ItemsSource = findings.Select(f => new InsightVm(
            f.Kind,
            f.Level,
            f.Title,
            f.Detail,
            (f.WindowStart is { } ws && f.WindowEnd is { } we
                ? $"时间范围 {ws:HH:mm:ss} - {we:HH:mm:ss}"
                : "") +
            (f.MissingSignals.Count > 0
                ? (f.WindowStart is { } ? " · " : "") + "缺失信号：" + string.Join("、", f.MissingSignals)
                : ""),
            KindLabel(f.Kind))).ToList();
    }

    private static string KindLabel(string kind) => kind switch
    {
        "CpuPressure" or "CpuLoad" => "CPU",
        "GpuSaturated" => "GPU",
        "VramPressure" or "VramLoad" or "VramUnknownTotal" => "显存",
        "MemoryPressure" => "内存",
        "MissingSignal" => "信号缺失",
        _ => "结论"
    };

    private sealed record InsightVm(
        string Kind, string Level, string Title, string Detail, string WindowText, string KindLabel);

    // ---------- 比较 ----------

    private void RefreshCompareSources()
    {
        var sessions = PerformanceSessionStore.LoadAll();
        var items = sessions.Select(s => new CompareSourceVm(s.Id, s.Name, s.StartedAt)).ToList();
        CompareA.ItemsSource = items;
        CompareB.ItemsSource = items;
    }

    private sealed record CompareSourceVm(string Id, string Name, DateTime StartedAt)
    {
        public string Display => $"{Name}（{StartedAt:MM-dd HH:mm}）";
    }

    private void Compare_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildCompare();

    private void RebuildCompare()
    {
        if (CompareA.SelectedItem is not CompareSourceVm va || CompareB.SelectedItem is not CompareSourceVm vb)
        {
            CompareHint.Text = "选择两个会话进行并排比较。";
            CompareRows.ItemsSource = null;
            return;
        }
        var store = PerformanceSessionStore.LoadAll();
        var a = store.FirstOrDefault(s => s.Id == va.Id);
        var b = store.FirstOrDefault(s => s.Id == vb.Id);
        if (a is null || b is null)
            return;
        if (a.Id == b.Id)
        {
            CompareHint.Text = "两个选择是同一个会话，无法比较。请选择不同的会话。";
            CompareRows.ItemsSource = null;
            return;
        }

        var sa = SessionStatistics.Summarize(a);
        var sb = SessionStatistics.Summarize(b);
        CompareHint.Text = $"A = {a.Name}（{a.StartedAt:MM-dd HH:mm}，{sa.SampleCount} 样本） · B = {b.Name}（{b.StartedAt:MM-dd HH:mm}，{sb.SampleCount} 样本）";

        var rows = new List<CompareRowVm>
        {
            Row("时长", FormatDuration(sa.Duration), FormatDuration(sb.Duration), null),
            Row("样本数", sa.SampleCount.ToString(), sb.SampleCount.ToString(), null)
        };
        rows.Add(PercentRow("CPU 平均 %", sa.Cpu?.Avg, sb.Cpu?.Avg));
        rows.Add(PercentRow("CPU 95 位 %", sa.Cpu?.HighP95, sb.Cpu?.HighP95));
        rows.Add(PercentRow("内存平均 %", sa.Mem?.Avg, sb.Mem?.Avg));
        rows.Add(PercentRow("GPU 平均 %", sa.Gpu?.Avg, sb.Gpu?.Avg));
        rows.Add(PercentRow("GPU 95 位 %", sa.Gpu?.HighP95, sb.Gpu?.HighP95));
        rows.Add(new CompareRowVm(
            "显存平均 MiB",
            sa.VramAvgMib?.ToString("0") ?? "不可用",
            sb.VramAvgMib?.ToString("0") ?? "不可用",
            sa.VramAvgMib is { } x && sb.VramAvgMib is { } y ? $"{y - x:+0;-0;0} MiB" : "不可用"));
        CompareRows.ItemsSource = rows;
    }

    private static CompareRowVm Row(string metric, string a, string b, string? delta)
        => new(metric, a, b, delta ?? "--");

    private static CompareRowVm PercentRow(string metric, double? a, double? b)
        => new(
            metric,
            a is { } av ? av.ToString("0.#") : "不可用",
            b is { } bv ? bv.ToString("0.#") : "不可用",
            a is { } av2 && b is { } bv2 ? $"{bv2 - av2:+0.#;-0.#;0}" : "不可用");

    private sealed record CompareRowVm(string Metric, string AVal, string BVal, string Delta);

    private static string FormatDuration(TimeSpan d)
        => d.TotalHours >= 1 ? $"{d.Hours}时{d.Minutes:00}分" : $"{d.Minutes}分{d.Seconds:00}秒";
}
