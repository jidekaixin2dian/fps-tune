using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 进程调度读写（亲和性掩码 + 优先级类）：游戏时后台应用"让路"的用户态通道。
/// 需要对目标进程有 PROCESS_SET_INFORMATION 权限；受保护/其他用户进程按不可用处理。
/// </summary>
internal static class NativeProcessScheduling
{
    internal const uint PriorityBelowNormal = 0x4000;

    internal static Func<int, (nuint Affinity, uint Priority)?>? ReadOverride { get; set; }
    internal static Func<int, nuint, bool>? WriteAffinityOverride { get; set; }
    internal static Func<int, uint, bool>? WritePriorityOverride { get; set; }

    /// <summary>读取当前亲和性与优先级；打不开或失败返回 null。</summary>
    internal static (nuint Affinity, uint Priority)? Read(int pid)
    {
        if (ReadOverride is not null) return ReadOverride(pid);
        var handle = Open(pid);
        if (handle == nint.Zero) return null;
        try
        {
            if (!GetProcessAffinityMask(handle, out var processMask, out var systemMask)) return null;
            var priority = GetPriorityClass(handle);
            return priority == 0 ? null : ((nuint)processMask, priority);
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>写入亲和性掩码；返回是否成功。</summary>
    internal static bool WriteAffinity(int pid, nuint mask)
    {
        if (WriteAffinityOverride is not null) return WriteAffinityOverride(pid, mask);
        var handle = Open(pid);
        if (handle == nint.Zero) return false;
        try { return SetProcessAffinityMask(handle, mask); }
        finally { CloseHandle(handle); }
    }

    /// <summary>写入优先级类；返回是否成功。</summary>
    internal static bool WritePriority(int pid, uint priorityClass)
    {
        if (WritePriorityOverride is not null) return WritePriorityOverride(pid, priorityClass);
        var handle = Open(pid);
        if (handle == nint.Zero) return false;
        try { return SetPriorityClass(handle, priorityClass); }
        finally { CloseHandle(handle); }
    }

    private static nint Open(int pid)
    {
        const uint queryLimited = 0x1000, setInformation = 0x0800;
        return OpenProcess(queryLimited | setInformation, inheritHandle: false, pid);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(nint process, out UIntPtr processAffinity, out UIntPtr systemAffinity);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessAffinityMask(nint process, UIntPtr processAffinity);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(nint process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(nint process, uint priorityClass);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
