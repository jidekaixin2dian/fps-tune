namespace FpsTune.Wpf.Services;

/// <summary>
/// 性能会话数据模型与统计（纯逻辑，可单测）。
/// 存储格式说明（schemaVersion=1）：
///   cpuPct / memPct / gpuPct：0-100 的百分比，null = 该样本不可用；
///   vramUsedMib：GPU 专用显存已用量（MiB），null = 不可用；
///   t：样本时间（本地时间，ISO 8601）。
/// </summary>
public sealed record SessionSamplePoint(
    DateTime T,
    double? CpuPct,
    double? MemPct,
    double? GpuPct,
    double? VramUsedMib);

public sealed record PerformanceSession(
    string Id,
    string Name,
    DateTime StartedAt,
    DateTime? EndedAt,
    int SchemaVersion,
    double IntervalSeconds,
    IReadOnlyList<SessionSamplePoint> Samples,
    double? VramTotalMib = null);

/// <summary>单指标的摘要统计。</summary>
public sealed record MetricStats(int Count, double Avg, double Peak, double LowP5, double HighP95);

public sealed record SessionSummary
{
    public int SampleCount { get; init; }
    public TimeSpan Duration { get; init; }
    public MetricStats? Cpu { get; init; }
    public MetricStats? Mem { get; init; }
    public MetricStats? Gpu { get; init; }
    /// <summary>显存用量（MiB）；null = 全程不可用。</summary>
    public double? VramAvgMib { get; init; }
    public double? VramPeakMib { get; init; }
    /// <summary>显存容量（MiB）；null = 未能可靠取得。</summary>
    public double? VramTotalMib { get; init; }
}

public static class SessionStatistics
{
    /// <summary>把采样缓冲汇总为摘要。样本缺失（null）的指标跳过；全部缺失则该指标为 null。</summary>
    public static SessionSummary Summarize(IReadOnlyList<SessionSamplePoint> samples, DateTime? startedAt, DateTime? endedAt)
    {
        var cpu = Stats(samples.Select(s => s.CpuPct));
        var mem = Stats(samples.Select(s => s.MemPct));
        var gpu = Stats(samples.Select(s => s.GpuPct));
        var vram = Stats(samples.Select(s => s.VramUsedMib));
        return new SessionSummary
        {
            SampleCount = samples.Count,
            Duration = startedAt is { } start && endedAt is { } end && end >= start ? end - start : TimeSpan.Zero,
            Cpu = cpu,
            Mem = mem,
            Gpu = gpu,
            VramAvgMib = vram?.Avg,
            VramPeakMib = vram?.Peak,
        };
    }

    public static SessionSummary Summarize(PerformanceSession session)
    {
        var summary = Summarize(session.Samples, session.StartedAt, session.EndedAt);
        return summary.VramTotalMib is null ? summary with { VramTotalMib = session.VramTotalMib } : summary;
    }

    private static MetricStats? Stats(IEnumerable<double?> values)
    {
        var finite = values.Where(v => v is double d && double.IsFinite(d)).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (finite.Count == 0)
            return null;
        return new MetricStats(
            finite.Count,
            Math.Round(finite.Average(), 1),
            Math.Round(finite[^1], 1),
            Percentile(finite, 0.05),
            Percentile(finite, 0.95));
    }

    /// <summary>最近邻秩百分位：p = 0.05 → 第 5 百分位（低位），p = 0.95 → 第 95 百分位（高位）。</summary>
    public static double Percentile(List<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0)
            return double.NaN;
        var idx = (int)Math.Floor(p * (sortedAsc.Count - 1));
        idx = Math.Clamp(idx, 0, sortedAsc.Count - 1);
        return Math.Round(sortedAsc[idx], 1);
    }
}

/// <summary>一条性能洞察（启发式规则输出）。</summary>
public sealed record InsightFinding(
    string Kind,
    string Level,
    string Title,
    string Detail,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    IReadOnlyList<string> MissingSignals);

/// <summary>
/// 性能洞察规则：只能基于已采样信号做保守判断，输出证据与缺失信号，
/// 并固定携带"启发式判断，不代表因果"的口径。
/// </summary>
public static class SessionInsights
{
    public const string HeuristicNotice = "启发式判断：仅描述采样期间信号的相关性，不代表因果，也不预测 FPS 提升。";

    public static List<InsightFinding> Evaluate(SessionSummary summary)
        => Evaluate(summary, DateTime.MinValue, DateTime.MinValue);

    public static List<InsightFinding> Evaluate(SessionSummary summary, DateTime windowStart, DateTime windowEnd)
    {
        var findings = new List<InsightFinding>();
        DateTime? ws = windowStart == DateTime.MinValue ? null : windowStart;
        DateTime? we = windowEnd == DateTime.MinValue ? null : windowEnd;

        // 缺失信号本身也是洞察证据：不能只提示 GPU 缺失而把 CPU、内存或
        // 显存全程不可用静默当成“没有压力”。
        if (summary.Cpu is null)
            findings.Add(Missing("CPU 指标全程不可用", "本次会话没有取得任何有效的 CPU 占用样本，无法评估 CPU 侧压力。", "cpu", ws, we));
        if (summary.Mem is null)
            findings.Add(Missing("内存指标全程不可用", "本次会话没有取得任何有效的系统内存占用样本，无法评估内存压力。", "mem", ws, we));
        if (summary.Gpu is null)
            findings.Add(Missing("GPU 指标全程不可用", "本次会话没有取得任何有效的 GPU 利用率样本（系统可能未提供 GPU Engine 性能计数器），无法评估 GPU 侧压力。", "gpu", ws, we));
        if (summary.VramAvgMib is null)
            findings.Add(Missing("显存用量指标全程不可用", "本次会话没有取得任何有效的专用显存用量样本，无法评估显存压力。", "vram", ws, we));

        if (summary.Cpu is { } cpu)
        {
            if (cpu.Avg >= 80 && cpu.HighP95 >= 90)
                findings.Add(new InsightFinding("CpuPressure", "attention",
                    "采样期间 CPU 占用持续偏高",
                    $"CPU 平均 {cpu.Avg}%、95 位 {cpu.HighP95}%、峰值 {cpu.Peak}%（{cpu.Count} 个样本）。若帧率同时偏低，常见于 CPU 侧瓶颈场景。",
                    ws, we, Array.Empty<string>()));
            else if (cpu.Avg >= 65)
                findings.Add(new InsightFinding("CpuLoad", "info",
                    "CPU 占用中等偏高",
                    $"CPU 平均 {cpu.Avg}%、95 位 {cpu.HighP95}%（{cpu.Count} 个样本），未达到持续高压阈值（平均 80 且 95 位 90）。",
                    ws, we, Array.Empty<string>()));
        }

        if (summary.Gpu is { } gpu)
        {
            if (gpu.Avg >= 90 && gpu.HighP95 >= 95)
                findings.Add(new InsightFinding("GpuSaturated", "attention",
                    "GPU 接近满载",
                    $"GPU 平均 {gpu.Avg}%、95 位 {gpu.HighP95}%、峰值 {gpu.Peak}%（{gpu.Count} 个样本）。GPU 满载时提高画质相关限制通常不再增加帧率。",
                    ws, we, Array.Empty<string>()));
        }
        if (summary.VramAvgMib is { } used)
        {
            if (summary.VramTotalMib is { } total && total > 0)
            {
                var ratio = used / total;
                if (ratio >= 0.9)
                    findings.Add(new InsightFinding("VramPressure", "attention",
                        "专用显存占用接近容量",
                        $"专用显存平均 {used:0} MiB / {total:0} MiB（约 {ratio:P0}），峰值 {summary.VramPeakMib:0} MiB。显存接近满载时可能出现帧时间尖峰或纹理降级。",
                        ws, we, Array.Empty<string>()));
                else if (ratio >= 0.75)
                    findings.Add(new InsightFinding("VramLoad", "info",
                        "专用显存占用偏高",
                        $"专用显存平均 {used:0} MiB / {total:0} MiB（约 {ratio:P0}），尚未接近容量阈值（90%）。",
                        ws, we, Array.Empty<string>()));
            }
            else
            {
                findings.Add(new InsightFinding("VramUnknownTotal", "info",
                    "显存容量未知，仅记录用量",
                    $"专用显存平均 {used:0} MiB、峰值 {summary.VramPeakMib:0} MiB；未能可靠取得显存容量，无法计算占比。",
                    ws, we, new[] { "vram-total" }));
            }
        }

        if (summary.Mem is { } mem)
        {
            if (mem.Avg >= 90)
                findings.Add(new InsightFinding("MemoryPressure", "attention",
                    "系统内存压力偏高",
                    $"内存占用平均 {mem.Avg}%、95 位 {mem.HighP95}%（{mem.Count} 个样本）。物理内存接近满载时 Windows 会增加换页，可能引入卡顿。",
                    ws, we, Array.Empty<string>()));
        }

        if (findings.Count == 0)
            findings.Add(new InsightFinding("None", "info",
                "未触发任何压力规则",
                "本次会话各项指标均未达到启发式阈值，或有效样本不足。这不代表没有优化空间。",
                ws, we, Array.Empty<string>()));
        return findings;
    }

    private static InsightFinding Missing(
        string title, string detail, string signal, DateTime? windowStart, DateTime? windowEnd)
        => new("MissingSignal", "info", title, detail, windowStart, windowEnd, new[] { signal });
}
