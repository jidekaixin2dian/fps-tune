namespace FpsTune.Wpf.Services;

internal sealed record ComparisonRow(string Metric, string AVal, string BVal, string Delta);
internal sealed record ComparisonHighlight(string Metric, string AVal, string BVal, string Delta, string Relative,
    string Trend, double AChartValue, double BChartValue, double Maximum);

/// <summary>One immutable comparison snapshot for the page and its offline report.</summary>
internal sealed record SessionComparison(PerformanceSession A, PerformanceSession B,
    IReadOnlyList<string> Issues, IReadOnlyList<ComparisonRow> Rows, IReadOnlyList<ComparisonHighlight> Highlights)
{
    internal static SessionComparison Create(PerformanceSession a, PerformanceSession b, SessionSummary sa, SessionSummary sb)
    {
        if (a.Id == b.Id) throw new ArgumentException(Str.T("Str.SameSessionCannotCompare"));
        var issues = SessionConditionRules.Differences(a, b);
        static string Known(string? value) => string.IsNullOrWhiteSpace(value) ? Str.T("Str.ConditionsUnknown") : value;
        static ComparisonRow Text(string key, string x, string y) => new(Str.T(key), x, y, "--");
        var highlights = new[] {
            Highlight(Str.T("Str.ConditionAverageFps"), Fps(a, f => f.AvgFps), Fps(b, f => f.AvgFps), "FPS", false, issues.Count == 0),
            Highlight(Str.T("Str.ReportColP1Low"), Fps(a, f => f.P1Low), Fps(b, f => f.P1Low), "FPS", false, issues.Count == 0),
            Highlight(Str.T("Str.ComparisonP99"), Fps(a, f => f.P99Ms), Fps(b, f => f.P99Ms), "ms", true, issues.Count == 0)
        };
        var rows = new List<ComparisonRow> {
            Text("Str.Duration", Duration(sa.Duration), Duration(sb.Duration)),
            Text("Str.SampleCount", sa.SampleCount.ToString(), sb.SampleCount.ToString()),
            Text("Str.ConditionTarget", Known(a.Conditions?.TargetSeconds?.ToString()), Known(b.Conditions?.TargetSeconds?.ToString())),
            Text("Str.ConditionInterval", Number(a.IntervalSeconds, "s"), Number(b.IntervalSeconds, "s")),
            Text("Str.ConditionGame", Known(a.Conditions?.GameName), Known(b.Conditions?.GameName)),
            Text("Str.ConditionResolution", SessionConditionRules.Resolution(a.Conditions), SessionConditionRules.Resolution(b.Conditions)),
            Text("Str.ConditionQuality", Known(a.Conditions?.Quality), Known(b.Conditions?.Quality)),
            Text("Str.ConditionScene", Known(a.Conditions?.Scene), Known(b.Conditions?.Scene))
        };
        rows.AddRange(highlights.Select(h => new ComparisonRow(h.Metric, h.AVal, h.BVal, h.Delta)));
        rows.Add(Text("Str.ComparisonFrameCount", a.Fps is { Samples: > 0 } af ? af.Samples.ToString() : Str.T("Str.Unavailable"),
            b.Fps is { Samples: > 0 } bf ? bf.Samples.ToString() : Str.T("Str.Unavailable")));
        rows.Add(Metric(Str.T("Str.ComparisonStutters"), Fps(a, f => f.Stutters), Fps(b, f => f.Stutters), ""));
        rows.Add(Metric(Str.T("Str.ConditionCpuAvg"), sa.Cpu?.Avg, sb.Cpu?.Avg, "%", Str.T("Str.ComparisonPoints")));
        rows.Add(Metric(Str.T("Str.ConditionCpu95"), sa.Cpu?.HighP95, sb.Cpu?.HighP95, "%", Str.T("Str.ComparisonPoints")));
        rows.Add(Metric(Str.T("Str.ConditionMemAvg"), sa.Mem?.Avg, sb.Mem?.Avg, "%", Str.T("Str.ComparisonPoints")));
        rows.Add(Metric(Str.T("Str.ConditionGpuAvg"), sa.Gpu?.Avg, sb.Gpu?.Avg, "%", Str.T("Str.ComparisonPoints")));
        rows.Add(Metric(Str.T("Str.ConditionGpu95"), sa.Gpu?.HighP95, sb.Gpu?.HighP95, "%", Str.T("Str.ComparisonPoints")));
        rows.Add(Metric(Str.T("Str.ConditionVramAvg"), sa.VramAvgMib, sb.VramAvgMib, "MiB"));
        return new(a, b, issues, rows, highlights);
    }
    private static double? Fps(PerformanceSession s, Func<SessionFpsStats, double> value)
        => s.Fps is { Samples: > 0 } f ? Finite(value(f)) : null;
    private static double? Finite(double? v) => v is { } d && double.IsFinite(d) && d >= 0 ? d : null;
    private static ComparisonRow Metric(string label, double? a, double? b, string unit, string? changeUnit = null)
        => new(label, Number(Finite(a), unit), Number(Finite(b), unit), Change(Finite(a), Finite(b), changeUnit ?? unit));
    private static ComparisonHighlight Highlight(string label, double? a, double? b, string unit, bool lowerBetter, bool comparable)
    {
        var delta = a is { } x && b is { } y && double.IsFinite(y - x) ? (double?)(y - x) : null;
        var percent = a is > 0 && delta is { } d && double.IsFinite(d / a.Value * 100) ? d / a.Value * 100 : (double?)null;
        var relative = percent is { } p ? p.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.CurrentCulture) + "%"
            : Str.T(a == 0 && b is not null ? "Str.ComparisonZeroBase" : "Str.Unavailable");
        var trend = !comparable || delta is null or 0 ? "neutral" : (lowerBetter ? delta < 0 : delta > 0) ? "good" : "attention";
        return new(label, Number(a, unit), Number(b, unit), Change(a, b, unit), relative, trend,
            a ?? 0, b ?? 0, Math.Max(1, Math.Max(a ?? 0, b ?? 0)));
    }
    private static string Number(double? value, string unit) => value is { } v && double.IsFinite(v)
        ? v.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + (unit.Length == 0 ? "" : " " + unit) : Str.T("Str.Unavailable");
    private static string Change(double? a, double? b, string unit) => a is { } x && b is { } y && double.IsFinite(y - x)
        ? (y - x).ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.CurrentCulture) + (unit.Length == 0 ? "" : " " + unit)
        : Str.T("Str.Unavailable");
    internal static string Duration(TimeSpan d) => d.TotalHours >= 1
        ? Str.T("Str.ConditionDurationHours", (int)d.TotalHours, d.Minutes)
        : Str.T("Str.ConditionDurationMinutes", (int)d.TotalMinutes, d.Seconds);
}
