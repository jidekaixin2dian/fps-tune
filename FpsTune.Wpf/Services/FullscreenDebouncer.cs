namespace FpsTune.Wpf.Services;

/// <summary>
/// 非对称去抖：进入要连续站稳，退出要早点放手。
///
/// 为什么不对称——全屏切换那 0.3-1.5 秒里，窗口样式、矩形与 DWM 状态会连着抖好几次：
/// 进得太快，用户在加载画面就看见桌面颜色跳；退得太慢，退出游戏后桌面一直偏色。
/// 所以进入用满窗口（默认 4/4），退出只要 2/4 就还原，再加一个冷却期防来回刷写。
///
/// <see cref="ForegroundScreenState.Unknown"/> 不进样本窗口：读不到信息时保持现状，
/// 这是「不被一次读失败打断」的关键。但「一直读不到」不能无限保持——全屏窗口关掉后
/// 前台要么变成 0，要么落回本程序自己的窗口，探针就再也读不出名字，只能一路返回 Unknown。
/// 所以已生效状态下给 Unknown 一个**有界宽限期**：超过它仍未恢复可读，就按退出处理。
/// </summary>
internal sealed class FullscreenDebouncer
{
    internal static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 读不到前台时的宽限。全屏窗口关闭（或被销毁）后 GetForegroundWindow 可能为 0、
    /// 也可能落回本程序窗口而读不出进程名；这种「读不到」若无限保持，用户就只能在退出程序时
    /// 才看到桌面颜色被还原。3 秒覆盖窗口销毁 + 前台重定向的过渡，又不会把交替读失败当成退出。
    /// </summary>
    internal static readonly TimeSpan DefaultUnknownGrace = TimeSpan.FromSeconds(3);

    private readonly Queue<bool> _samples = new();
    private readonly object _lock = new();
    private readonly int _window;
    private readonly int _enterSamples;
    private readonly int _exitSamples;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _unknownGrace;

    private bool _decided;
    private bool _committed;
    private DateTime _lastChange = DateTime.MinValue;
    private DateTime _unknownSince = DateTime.MinValue;

    internal FullscreenDebouncer(int window = 4, int enterSamples = 4, int exitSamples = 2,
        TimeSpan? cooldown = null, TimeSpan? unknownGrace = null)
    {
        if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
        if (enterSamples < 1 || enterSamples > window) throw new ArgumentOutOfRangeException(nameof(enterSamples));
        if (exitSamples < 1 || exitSamples > window) throw new ArgumentOutOfRangeException(nameof(exitSamples));
        if (unknownGrace is { } grace && grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(unknownGrace));
        _window = window;
        _enterSamples = enterSamples;
        _exitSamples = exitSamples;
        _cooldown = cooldown ?? DefaultCooldown;
        _unknownGrace = unknownGrace ?? DefaultUnknownGrace;
    }

    /// <summary>已确认的状态：true = 应处于「全屏方案已生效」。</summary>
    internal bool Committed => _committed;

    /// <summary>是否已经形成过明确判定（未判定时首个非全屏样本不会触发任何动作）。</summary>
    internal bool HasDecision => _decided;

    internal int SampleCount => _samples.Count;

    /// <summary>喂一个样本；返回 true 表示状态应**翻转**（调用方据此应用或还原）。</summary>
    /// <remarks>
    /// Update 跑在守护 Timer 线程，Reset 跑在 UI 线程（改设置/暂停时立刻重起算）；
    /// 两者无别的互斥，队列与判定字段必须在本锁内读写，否则并发 Clear/Enqueue 会炸或错判。
    /// </remarks>
    internal bool Update(ForegroundScreenState state, DateTime now)
    {
        lock (_lock)
        {
        if (state == ForegroundScreenState.Unknown)
        {
            // 还没生效时读不到就是读不到，不计数（进入阶段的抖动仍被丢弃）。
            if (!_committed) return false;
            if (_unknownSince == DateTime.MinValue) _unknownSince = now;
            if (_cooldown > TimeSpan.Zero && _lastChange != DateTime.MinValue && now - _lastChange < _cooldown)
                return false;
            // 一直在生效状态下读不到前台，说明全屏窗口已经不在了：宽限期一过就还原。
            return now - _unknownSince >= _unknownGrace && Commit(false, now);
        }

        _unknownSince = DateTime.MinValue;

        var isFullscreen = state != ForegroundScreenState.NotFullscreen;
        _samples.Enqueue(isFullscreen);
        while (_samples.Count > _window) _samples.Dequeue();

        var full = _samples.Count(sample => sample);
        var empty = _samples.Count - full;

        if (!_decided)
        {
            if (isFullscreen && full >= _enterSamples) return Commit(true, now);
            // 首次就稳定在非全屏：只确立判定，不触发动作。
            if (!isFullscreen && empty >= _enterSamples) _decided = true;
            return false;
        }

        if (_cooldown > TimeSpan.Zero && _lastChange != DateTime.MinValue && now - _lastChange < _cooldown)
            return false;

        if (!_committed && full >= _enterSamples) return Commit(true, now);
        if (_committed && empty >= _exitSamples) return Commit(false, now);
        return false;
        }
    }

    private bool Commit(bool fullscreen, DateTime now)
    {
        _decided = true;
        _committed = fullscreen;
        _lastChange = now;
        _unknownSince = DateTime.MinValue;
        return true;
    }

    /// <summary>开关、方案或通道变化后重新起算，避免旧样本把新配置直接判成已生效。</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _samples.Clear();
            _decided = false;
            _committed = false;
            _lastChange = DateTime.MinValue;
            _unknownSince = DateTime.MinValue;
        }
    }
}
