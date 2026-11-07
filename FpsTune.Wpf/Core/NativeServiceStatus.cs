using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Core;

/// <summary>服务的启动类型（对应 SERVICE_*_START 常量）。</summary>
internal enum ServiceStartType
{
    /// <summary>读取失败或值不在已知范围——如实返回未知，不猜。</summary>
    Unknown = -1,
    Boot = 0,
    System = 1,
    Auto = 2,
    Demand = 3,
    Disabled = 4
}

internal static class NativeServiceStatus
{
    // SC_MANAGER_CONNECT / SERVICE_QUERY_STATUS only; no mutation access requested.
    internal static string Read(string name)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, name, 4);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Status>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.CurrentState switch
                {
                    1 => "STOPPED", 2 => "START_PENDING", 3 => "STOP_PENDING", 4 => "RUNNING",
                    5 => "CONTINUE_PENDING", 6 => "PAUSE_PENDING", 7 => "PAUSED",
                    _ => throw new InvalidOperationException("Unknown service state")
                };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    /// <summary>
    /// 读服务的启动类型（SERVICE_QUERY_CONFIG，只读）。
    /// 服务不存在、权限不足、结构体读不出来都返回 <see cref="ServiceStartType.Unknown"/>，
    /// 由调用方显示「不可用」——体检里编一个「自动」比不给更糟。
    /// </summary>
    internal static ServiceStartType ReadStartType(string name)
    {
        try
        {
            var manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) return ServiceStartType.Unknown;
            try
            {
                var service = OpenService(manager, name, ServiceQueryConfig);
                if (service == IntPtr.Zero) return ServiceStartType.Unknown;
                try
                {
                    // 第一次调用只为拿所需缓冲区大小，失败时 needed 即额度。
                    _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
                    if (needed == 0 || needed > MaxConfigBuffer) return ServiceStartType.Unknown;

                    var buffer = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        if (!QueryServiceConfig(service, buffer, needed, out _))
                            return ServiceStartType.Unknown;
                        var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                        return MapStartType(config.StartType);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return ServiceStartType.Unknown;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException)
        {
            return ServiceStartType.Unknown;
        }
    }

    /// <summary>dwStartType → 枚举（纯映射，测试锁死）。</summary>
    internal static ServiceStartType MapStartType(uint startType) => startType switch
    {
        0 => ServiceStartType.Boot,
        1 => ServiceStartType.System,
        2 => ServiceStartType.Auto,
        3 => ServiceStartType.Demand,
        4 => ServiceStartType.Disabled,
        _ => ServiceStartType.Unknown
    };

    private const uint ServiceQueryConfig = 0x0001;

    /// <summary>查询配置的正常大小是几百字节；超过这个数说明读数异常，直接按未知处理。</summary>
    private const uint MaxConfigBuffer = 64 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    /// <summary>QUERY_SERVICE_CONFIGW：字符串字段是相对结构体的指针，只取 dwStartType。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out Status status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint bufferSize, out uint bytesNeeded);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
