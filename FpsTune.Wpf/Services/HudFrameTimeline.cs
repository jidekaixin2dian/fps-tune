namespace FpsTune.Wpf.Services;

/// <summary>HUD 帧时间折线的单次快照：最近窗口内的帧序列、中位数、卡顿点与滚动 1% Low。</summary>
public sealed record HudTimelineSnapshot(
    IReadOnlyList<double> Frames,
    double MedianMs,
    IReadOnlyList<int> StutterIndices,
    double P1Low);

/// <summary>
/// HUD 帧时间折线的数据缓冲与判定（0.5.0 M4，纯逻辑）。
/// 缓冲保存最近 <see cref="WindowFrames"/> 帧；卡顿判定 = 帧时间 &gt; 2× 窗口中位数
/// （与游戏负载相关的自适应阈值）；滚动 1% Low 沿用 FrameTimeStats 的口径（1000/p99 帧时间）。
/// HUD 不可见时不调用 Append，零采样开销。
/// </summary>
public sealed class HudFrameTimeline
{
    internal const int WindowFrames = 60;
    internal const double StutterMultiple = 2.0;

    private readonly object _gate = new();
    private readonly double[] _frames = new double[WindowFrames];
    private int _count;

    public void Append(double ms)
    {
        if (!double.IsFinite(ms) || ms <= 0) return;
        lock (_gate)
        {
            if (_count < WindowFrames)
            {
                _frames[_count++] = ms;
                return;
            }
            Array.Copy(_frames, 1, _frames, 0, WindowFrames - 1);
            _frames[WindowFrames - 1] = ms;
        }
    }

    public void Clear()
    {
        lock (_gate) { _count = 0; Array.Clear(_frames); }
    }

    /// <summary>窗口数据不足 30 帧时返回 null（与 LiveFrameWindow 的最小样本口径一致）。</summary>
    public HudTimelineSnapshot? Analyze()
    {
        double[] frames;
        lock (_gate) frames = _frames[.._count];
        if (frames.Length < 30) return null;
        var sorted = frames.OrderBy(v => v).ToArray();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
        var threshold = median > 0 ? median * StutterMultiple : double.PositiveInfinity;
        var stutters = new List<int>();
        for (var i = 0; i < frames.Length; i++)
            if (frames[i] > threshold) stutters.Add(i);
        var p99Ms = sorted[Math.Min((int)Math.Floor(sorted.Length * .99), sorted.Length - 1)];
        return new HudTimelineSnapshot(frames, median, stutters, 1000 / p99Ms);
    }
}
