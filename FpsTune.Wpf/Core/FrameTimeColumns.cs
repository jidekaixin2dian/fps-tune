using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>一次采样里各延迟相关列的逐帧序列（0.5.0 M5 延迟预算的输入）。空数组 = 该列不存在（如实不可测）。</summary>
public sealed record FrameColumnSeries(
    IReadOnlyList<double> FrameTimesMs,
    IReadOnlyList<double> GpuBusyMs,
    IReadOnlyList<double> CpuBusyMs,
    IReadOnlyList<double> DisplayLatencyMs);

/// <summary>
/// PresentMon CSV 的多列解析（0.5.0 M5）：按语义列名候选表兼容 v1/v2 输出。
/// v2 默认列：FrameTime / CPUBusy / GPUBusy / DisplayLatency；
/// v1（1.9.x）列：msBetweenPresents / msInPresentAPI / msGPUActive / msUntilDisplayed。
/// 帧时间列缺失返回 null（调用方按采样器不兼容处理）；其余列缺失为空数组（对应段「不可测」）。
/// </summary>
public static class FrameTimeColumns
{
    internal static readonly (string Semantic, string[] Candidates)[] Columns =
    [
        ("frame", ["FrameTime", "msBetweenPresents"]),
        ("gpu", ["GPUBusy", "msGPUActive"]),
        ("cpu", ["CPUBusy", "msInPresentAPI"]),
        ("display", ["DisplayLatency", "msUntilDisplayed"]),
    ];

    public static FrameColumnSeries? Read(IEnumerable<string> lines)
    {
        using var rows = lines.GetEnumerator();
        if (!rows.MoveNext()) return null;
        var header = FrameTimeStats.ReadHeader(rows.Current);
        if (!rows.MoveNext()) return null;

        var indices = new int[Columns.Length];
        for (var c = 0; c < Columns.Length; c++)
        {
            indices[c] = -1;
            foreach (var candidate in Columns[c].Candidates)
            {
                var index = Array.FindIndex(header, h => h.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) { indices[c] = index; break; }
            }
        }
        if (indices[0] < 0) return null; // 帧时间列必须存在

        var frame = new List<double>();
        var gpu = indices[1] >= 0 ? new List<double>() : [];
        var cpu = indices[2] >= 0 ? new List<double>() : [];
        var display = indices[3] >= 0 ? new List<double>() : [];
        do
        {
            if (FrameTimeStats.TryReadFrame(rows.Current, indices[0], isFps: false, out var frameMs))
            {
                frame.Add(frameMs);
                ReadOptional(rows.Current, indices[1], gpu);
                ReadOptional(rows.Current, indices[2], cpu);
                ReadOptional(rows.Current, indices[3], display);
            }
        } while (rows.MoveNext());

        return new FrameColumnSeries(frame, gpu, cpu, display);
    }

    private static void ReadOptional(string line, int column, List<double> target)
    {
        if (column < 0) return;
        var position = 0;
        for (var index = 0; FrameTimeStats.NextField(line, ref position, out var field); index++)
        {
            if (index != column) continue;
            if (double.TryParse(field, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value > 0)
                target.Add(value);
            return;
        }
    }
}
