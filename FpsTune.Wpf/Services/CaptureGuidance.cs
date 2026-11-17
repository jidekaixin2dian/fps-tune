namespace FpsTune.Wpf.Services;

/// <summary>
/// 采样失败原因的统一措辞与 ReasonCode 常量。
/// <para>
/// 同一个失败原因会出现在三个地方：<see cref="CaptureRun.Error"/>（界面卡片、CLI <c>-Capture</c> 的 error 字段）、
/// 对照结论卡的失败行（<see cref="ComparisonPlanner"/>）、以及落盘的 run.json。措辞只在这里维护一份，
/// 避免"同一次失败三种说法"——本轮之前编排层与对照层各写一套，改一处会漏掉另一处。
/// </para>
/// <para>
/// 这些是服务层硬编码中文而不是界面资源键：CLI 不进语言层，<b>ReasonCode 才是给机器看的稳定契约</b>，
/// 人类可读的那句话永远从这里出。新增原因时同时补 <see cref="Describe"/> 与 <c>I18nBaseline</c> 计数。
/// </para>
/// </summary>
internal static class CaptureGuidance
{
    // ReasonCode 的唯一来源：采样管线内部判定、run.json 与对照结论都读这些常量。
    internal const string NoSampler = "no-sampler";
    internal const string NoProcess = "no-process";
    internal const string NoPermission = "no-permission";
    /// <summary>会话建立成功、ETW 事件也在产生，但一帧都没归属到目标进程。</summary>
    internal const string EventsLost = "etw-events-lost";
    internal const string SampleFailed = "sample-failed";
    internal const string IncompatibleSampler = "incompatible-sampler";
    internal const string ParseFailed = "parse-failed";
    internal const string Busy = "busy";
    internal const string Unexpected = "unexpected";

    /// <summary>ReasonCode → 给用户看的完整句子（说清发生了什么、下一步怎么做）。未知 code 用调用方兜底。</summary>
    internal static string Describe(string? reasonCode, string? fallback = null) => reasonCode switch
    {
        NoSampler => "没有找到可用的帧率采样器（内置采样器不可用，也没有已安装的 PresentMon）。",
        NoProcess => "没有找到目标进程：请把游戏开到要测量的画面后重试。",
        NoPermission => "采样被系统拒绝：PresentMon 的 ETW 会话需要管理员权限。请以管理员身份运行后重试。",
        EventsLost => EventsLostText(0),
        SampleFailed => "采样没有产生有效数据（目标进程可能已退出，或采样器版本不受支持）。",
        IncompatibleSampler => "采样器输出的列无法识别（采样器版本可能不兼容），原始数据已保留。",
        ParseFailed => "采样数据解析失败。",
        Busy => "已有一次采样正在进行，请等它结束后再试。",
        _ => fallback ?? "采样失败。",
    };

    /// <summary>
    /// 「0 帧但图形事件确实在产生」的专用说明。本机实测形态：PresentMon 会话建立成功、退出码 0、
    /// 10 秒丢 5.6–14 万条 ETW 事件、0 行 CSV —— 事件被系统或反作弊拦在了采样器之外。
    /// 只知道原因不知道条数时（对照结论卡只有 ReasonCode）传 0，措辞自动省掉数字。
    /// </summary>
    internal static string EventsLostText(long lostEvents)
    {
        var head = lostEvents > 0
            ? $"采样会话建立成功，但一帧都没拿到：本机拦下了图形事件（报告丢了 {lostEvents:N0} 条 ETW 事件）。"
            : "采样会话建立成功，但一帧都没拿到：本机拦下了图形事件。";
        return head
            + "这通常出现在远程桌面或虚拟显示会话里，也可能是反作弊内核驱动正在运行。"
            + "先换到本机直连显示器的会话重试；要采的游戏带反作弊时，退出游戏后再采一次。";
    }
}
