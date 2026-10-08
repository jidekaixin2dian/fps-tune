using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Core;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out Status status, int size, out int needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
