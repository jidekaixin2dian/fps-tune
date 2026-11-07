namespace FpsTune.Wpf.Core;

/// <summary>
/// 键鼠缓冲区深度策略（纯函数，不碰注册表、不读设置）。
///
/// 深度写入 kbdclass / mouclass 的 DataQueueSize，控制驱动事件队列能排多少条事件。
/// 队列越浅，卡顿超过「能兜住的时间」后新事件会被直接丢弃；丢掉的若是「松开」，
/// 游戏会当按键一直按着（一直开枪、准星乱飞、松手还在走）。
///
/// 公式：能兜住的毫秒数 = 深度 / 报告率 × 1000（报告率即鼠标每秒上报次数）。
/// 例：1000 Hz 下深度 50 只能兜住 50 毫秒，8000 Hz 下同样 50 只能兜住 6 毫秒。
///
/// 推荐值 = 让覆盖时长达到 <see cref="TargetCoverageMs"/> 所需的最小深度，
/// 再对齐到 <see cref="Step"/> 的整数倍并夹进合法区间。
/// </summary>
public static class InputQueuePolicy
{
    /// <summary>合法下界：再低连一次常规卡顿都撑不住，且驱动会按默认值处理。</summary>
    public const int MinDepth = 16;

    /// <summary>合法上界：再深只会让恢复输入时排队更久，卡顿容错收益已经饱和。</summary>
    public const int MaxDepth = 1024;

    /// <summary>深度步长：界面与写入都按 8 对齐，避免出现 37 这种无从复现的怪值。</summary>
    public const int Step = 8;

    /// <summary>kbdclass / mouclass 未设置时驱动自带的默认深度。</summary>
    public const int WindowsDefaultDepth = 100;

    /// <summary>
    /// 推荐覆盖时长（毫秒）。取 96 是有意的：
    /// 1000 Hz 下推荐深度正好是 96，低于驱动默认的 100——默认配置不会被误报成「偏低」，
    /// 只有用户把深度压到 50 这类老值时才会提示。
    /// </summary>
    public const int TargetCoverageMs = 96;

    /// <summary>键盘没有报告率概念，按固定推荐值；与 1000 Hz 鼠标的推荐值同档。</summary>
    public const int KeyboardRecommendedDepth = 96;

    /// <summary>界面上的报告率档位（Hz）。</summary>
    public static readonly int[] PollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];

    /// <summary>深度在报告率下能兜住多少毫秒（四舍五入到整数毫秒）。</summary>
    public static int CoverageMs(int depth, int pollingRate)
        => depth <= 0 || pollingRate <= 0
            ? 0
            : (int)Math.Round(depth * 1000d / pollingRate, MidpointRounding.AwayFromZero);

    /// <summary>按报告率算出的推荐深度（覆盖时长至少 <see cref="TargetCoverageMs"/> 毫秒）。</summary>
    public static int RecommendedDepth(int pollingRate)
        => pollingRate <= 0
            ? MinDepth
            : Normalize((int)Math.Ceiling(TargetCoverageMs * pollingRate / 1000d));

    /// <summary>鼠标深度是否低于该报告率下的推荐值。</summary>
    public static bool IsBelowRecommended(int depth, int pollingRate)
        => depth < RecommendedDepth(pollingRate);

    /// <summary>键盘深度是否低于固定推荐值。</summary>
    public static bool IsKeyboardBelowRecommended(int depth)
        => depth < KeyboardRecommendedDepth;

    /// <summary>合法深度：区间内且按 <see cref="Step"/> 对齐。</summary>
    public static bool IsValidDepth(int depth)
        => depth is >= MinDepth and <= MaxDepth && depth % Step == 0;

    /// <summary>
    /// 把任意输入夹进合法区间并按 <see cref="Step"/> 向上对齐：
    /// 往下对齐等于偷偷削弱缓冲，宁可多 4 条也不给不够的量。
    /// 例：100 → 104，50 → 56，16 → 16，2000 → 1024。
    /// </summary>
    public static int Normalize(int depth)
    {
        var clamped = Math.Clamp(depth, MinDepth, MaxDepth);
        var snapped = (clamped + Step - 1) / Step * Step;
        return Math.Clamp(snapped, MinDepth, MaxDepth);
    }

    /// <summary>报告率是否是界面支持的档位。</summary>
    public static bool IsPollingRate(int pollingRate) => Array.IndexOf(PollingRates, pollingRate) >= 0;

    /// <summary>
    /// 三态判定：未设置（注册表没有该值）/ 已设置但低于推荐 / 已达标。
    /// 「未设置」不等于「未达标」：系统此时用的是驱动默认值，和用户主动压低是两回事。
    /// </summary>
    public static InputQueueState Evaluate(int? storedDepth, int pollingRate, bool keyboard)
    {
        if (storedDepth is not { } depth) return InputQueueState.NotSet;
        var below = keyboard ? IsKeyboardBelowRecommended(depth) : IsBelowRecommended(depth, pollingRate);
        return below ? InputQueueState.BelowRecommended : InputQueueState.Ok;
    }
}

/// <summary>键鼠缓冲区的三种状态。</summary>
public enum InputQueueState
{
    /// <summary>注册表里没有该值，驱动用默认深度。</summary>
    NotSet,

    /// <summary>有值，但低于按报告率算出的推荐值。</summary>
    BelowRecommended,

    /// <summary>有值且不低于推荐值。</summary>
    Ok
}
