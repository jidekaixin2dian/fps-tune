using System.Runtime.InteropServices;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>
/// Windows 计时器分辨率（100ns 单位）读写：ISLC / TimerResolution 等社区工具的标准做法。
/// 0.5 ms = 5000。Windows 10 2004+ 对后台进程请求有合并策略，实际效果因系统版本与
/// 游戏引擎而异——产品文案如实说明，收益由用户用性能会话 A/B 自行验证。
/// </summary>
internal static class NativeTimerResolution
{
    internal static Func<(uint Min, uint Max, uint Current)>? QueryOverride { get; set; }
    internal static Func<uint, bool, uint>? SetOverride { get; set; }

    /// <summary>当前生效的计时器分辨率与系统上下限（100ns 单位）。</summary>
    internal static (uint Min, uint Max, uint Current) Query()
    {
        if (QueryOverride is not null) return QueryOverride();
        var status = NtQueryTimerResolution(out var min, out var max, out var current);
        if (status != 0) throw new InvalidOperationException(Str.T("Str.TimerQueryFailed", status));
        return (min, max, current);
    }

    /// <summary>请求（enable=true）或撤销（false）本进程的分辨率请求；返回当前生效值。</summary>
    internal static uint Set(uint desired, bool enable)
    {
        if (SetOverride is not null) return SetOverride(desired, enable);
        var status = NtSetTimerResolution(desired, enable, out var current);
        if (status != 0) throw new InvalidOperationException(Str.T("Str.TimerSetFailed", status));
        return current;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetTimerResolution(uint desiredResolution, [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);
}
