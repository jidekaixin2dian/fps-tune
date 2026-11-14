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
    /// <summary>超过调用方的完整统计容量；不返回截断统计。</summary>
    TooManyFrames,
}

public sealed record FrameParseResult(
    FrameSummary? Frame,
    FrameParseError? Error,
    int FrameCount,
    IReadOnlyList<string> Header);

/// <summary>逐帧帧时间序列（0.5.0：前后对照切段与延迟预算共用）。失败时 Error 说明原因、Header 携带实际表头。</summary>
public sealed record FrameSeriesResult(
    IReadOnlyList<double> FrameTimesMs,
    FrameParseError? Error,
    IReadOnlyList<string> Header);

/// <summary>
/// PresentMon CSV 的帧时间解析（0.2.0 C-E 从 ExperimentRunner 抽出共用：
/// 实验管线与会话 FPS 走同一套口径——列名候选、&gt;0 过滤、最少 30 帧、
/// p99 帧时间、卡顿 = 帧时间 &gt; 50ms 的帧数）。
/// </summary>
public static class FrameTimeStats
{
    internal const int MaxFrameCount = 2_000_000;
    internal const int MaxCsvLineChars = 16384;
    private static readonly string[] ColumnCandidates = ["msBetweenPresents", "FrameTime", "frame_time", "FPS"];
    private const int MinFrames = 30;

    public static FrameParseResult Parse(IEnumerable<string> lines)
        => Parse(lines, CancellationToken.None, int.MaxValue);

    internal static FrameParseResult Parse(IEnumerable<string> lines, CancellationToken token, int maxFrames)
    {
        var series = ReadSeries(lines, token, maxFrames);
        if (series.Error is { } error)
            return new FrameParseResult(null, error, series.FrameTimesMs.Count, series.Header);
        var frameTimes = series.FrameTimesMs;
        if (frameTimes.Count < MinFrames)
            return new FrameParseResult(null, FrameParseError.TooFewFrames, frameTimes.Count, series.Header);

        token.ThrowIfCancellationRequested();
        var sorted = frameTimes.OrderBy(v => v).ToList();
        // 最近秩（nearest-rank）p99：第 ceil(0.99·N) 个值。旧式 floor(N·0.99) 在 N 恰为
        // 100 的整倍数时多取一位（把第 99.99 位的值当 p99）；N<100 时 nearest-rank 本身
        // 就是最差帧，这是秩百分位的小样本固有语义，不是实现问题。
        var p99Ms = sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * 0.99) - 1, 0, sorted.Count - 1)];
        var avgMs = frameTimes.Average();
        var avgFps = 1000.0 / avgMs;
        var p1Fps = 1000.0 / p99Ms;
        var stutters = sorted.Count(v => v > 50);
        token.ThrowIfCancellationRequested();

        return new FrameParseResult(
            new FrameSummary(
                frameTimes.Count,
                Math.Round(avgFps, 2),
                Math.Round(p1Fps, 2),
                Math.Round(p99Ms, 2),
                stutters),
            null,
            frameTimes.Count,
            series.Header);
    }

    /// <summary>逐帧帧时间序列：与 Parse 同一套列名候选、&gt;0 过滤与容量上限，供对照切段等消费原始帧。</summary>
    internal static FrameSeriesResult ReadSeries(IEnumerable<string> lines, CancellationToken token, int maxFrames)
    {
        token.ThrowIfCancellationRequested();
        using var rows = lines.GetEnumerator();
        if (!rows.MoveNext())
            return new FrameSeriesResult([], FrameParseError.TooFewLines, []);
        var header = ReadHeader(rows.Current);
        if (!rows.MoveNext())
            return new FrameSeriesResult([], FrameParseError.TooFewLines, header);
        var col = FindColumn(header);
        if (col < 0)
            return new FrameSeriesResult([], FrameParseError.UnknownColumns, header);

        var frameTimes = new List<double>();
        var isFps = header[col].Trim().Equals("FPS", StringComparison.OrdinalIgnoreCase);
        do
        {
            token.ThrowIfCancellationRequested();
            if (TryReadFrame(rows.Current, col, isFps, out var ms))
            {
                // 溢出帧计入列表（FrameCount 语义与既有 Parse 逐字一致），统计由调用方按 Error 不产出。
                if (frameTimes.Count >= maxFrames)
                {
                    frameTimes.Add(ms);
                    return new FrameSeriesResult(frameTimes, FrameParseError.TooManyFrames, header);
                }
                frameTimes.Add(ms);
            }
        } while (rows.MoveNext());
        token.ThrowIfCancellationRequested();
        return new FrameSeriesResult(frameTimes, null, header);
    }

    internal static string[] ReadHeader(string line)
    {
        var fields = new List<string>(); var position = 0;
        var text = line.AsSpan().TrimStart('\uFEFF');
        while (NextField(text, ref position, out var field)) fields.Add(field.ToString());
        return fields.ToArray();
    }

    internal static int FindColumn(string[] header)
    {
        foreach (var candidate in ColumnCandidates)
        {
            var column = Array.FindIndex(header, h => h.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (column >= 0) return column;
        }
        return -1;
    }

    internal static bool TryReadFrame(string line, int column, bool isFps, out double milliseconds)
    {
        milliseconds = 0; var position = 0;
        for (var index = 0; NextField(line, ref position, out var field); index++)
        {
            if (index != column) continue;
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value <= 0) return false;
            milliseconds = isFps ? 1000.0 / value : value;
            return double.IsFinite(milliseconds) && milliseconds > 0;
        }
        return false;
    }

    // PresentMon records are single-line. Commas inside quoted application/extra fields must not shift the frame column.
    internal static bool NextField(ReadOnlySpan<char> line, ref int position, out ReadOnlySpan<char> field)
    {
        field = default;
        if (position > line.Length) return false;
        var start = position; var quoted = false;
        for (; position < line.Length; position++)
        {
            if (line[position] == '"') quoted = !quoted;
            else if (line[position] == ',' && !quoted) break;
        }
        if (quoted) { position = line.Length + 1; return false; }
        field = line[start..position].Trim(); position++;
        if (field.Length >= 2 && field[0] == '"' && field[^1] == '"') field = field[1..^1].Trim();
        return true;
    }
}
