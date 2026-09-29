using System.Globalization;

namespace FpsTune.Wpf.Services;

/// <summary>一次帧时间采样的汇总（PresentMon CSV 解析产物）。纯逻辑，可单测。</summary>
public sealed record FrameSummary(int Samples, double AvgFps, double P1Low, double P99Ms, int Stutters);

/// <summary>解析失败原因（结构化——中文文案由各消费方自行组装，本层不携带文案）。</summary>
public enum FrameParseError
{
    /// <summary>行数不足（无表头或只有表头）。</summary>
    TooFewLines,
    /// <summary>表头里找不到帧时间列（Header 携带实际表头）。</summary>
    UnknownColumns,
    /// <summary>正帧数 &lt; 30（FrameCount 携带实际数量）。</summary>
    TooFewFrames,
}

public sealed record FrameParseResult(
    FrameSummary? Frame,
    FrameParseError? Error,
    int FrameCount,
    IReadOnlyList<string> Header);

/// <summary>
/// PresentMon CSV 的帧时间解析（0.2.0 C-E 从 ExperimentRunner 抽出共用：
/// 实验管线与会话 FPS 走同一套口径——列名候选、&gt;0 过滤、最少 30 帧、
/// p99 帧时间、卡顿 = 帧时间 &gt; 50ms 的帧数）。
/// </summary>
public static class FrameTimeStats
{
    private static readonly string[] ColumnCandidates = ["msBetweenPresents", "frame_time", "FPS", "fps"];
    private const int MinFrames = 30;

    public static FrameParseResult Parse(IEnumerable<string> lines)
    {
        var all = lines as IList<string> ?? lines.ToList();
        if (all.Count < 2)
            return new FrameParseResult(null, FrameParseError.TooFewLines, 0, []);

        var header = all[0].Split(',');
        var col = -1;
        foreach (var candidate in ColumnCandidates)
        {
            col = Array.FindIndex(header, h => h.Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (col >= 0)
                break;
        }
        if (col < 0)
            return new FrameParseResult(null, FrameParseError.UnknownColumns, 0, header);

        var frameTimes = new List<double>();
        for (var i = 1; i < all.Count; i++)
        {
            var line = all[i];
            if (line.Length == 0)
                continue;
            var fields = line.Split(',');
            if (col >= fields.Length)
                continue;
            if (double.TryParse(fields[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
                frameTimes.Add(n);
        }
        if (frameTimes.Count < MinFrames)
            return new FrameParseResult(null, FrameParseError.TooFewFrames, frameTimes.Count, header);

        var sorted = frameTimes.Order().ToList();
        var p99Ms = sorted[Math.Min((int)Math.Floor(sorted.Count * 0.99), sorted.Count - 1)];
        var avgMs = frameTimes.Average();
        var avgFps = 1000.0 / avgMs;
        var p1Fps = 1000.0 / p99Ms;
        var stutters = sorted.Count(v => v > 50);

        return new FrameParseResult(
            new FrameSummary(
                frameTimes.Count,
                Math.Round(avgFps, 2),
                Math.Round(p1Fps, 2),
                Math.Round(p99Ms, 2),
                stutters),
            null,
            frameTimes.Count,
            header);
    }
}
