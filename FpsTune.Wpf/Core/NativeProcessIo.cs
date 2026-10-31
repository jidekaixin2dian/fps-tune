using System.Runtime.InteropServices;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>进程累计 IO 字节（GetProcessIoCounters，文档化 kernel32 接口）；用于采样窗口内的磁盘增量。</summary>
internal static class NativeProcessIo
{
    internal static Func<int, ulong?>? ReadOverride { get; set; }

    /// <summary>读+写传输字节合计；进程打不开或受保护时返回 null（如实按不可用处理）。</summary>
    internal static ulong? TotalBytes(int pid)
    {
        if (ReadOverride is not null) return ReadOverride(pid);
        var handle = OpenProcess(ProcessQueryInformation, inheritHandle: false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return GetProcessIoCounters(handle, out var counters)
                ? counters.ReadTransferCount + counters.WriteTransferCount
                : null;
        }
        finally { CloseHandle(handle); }
    }

    private const uint ProcessQueryInformation = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IO_COUNTERS counters);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
