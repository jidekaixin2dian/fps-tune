using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record MemorySnapshot(ulong Total, ulong Available, ulong? LowPriorityStandby);
internal interface IMemoryCleanupApi
{
    MemorySnapshot Read();
    void PurgeLowPriorityStandby();
}

internal sealed class MemoryCleanupService(IMemoryCleanupApi? api = null, Func<bool>? isBusy = null, Func<bool>? isAdministrator = null)
{
    private readonly IMemoryCleanupApi _api = api ?? new WindowsMemoryCleanupApi();
    internal bool IsAdministrator => (isAdministrator ?? AdminHelper.IsAdministrator)();
    internal bool IsBusy => (isBusy ?? (() => App.SessionService.IsRunning || ExperimentRunner.IsRunning))();
    internal sealed record Result(MemorySnapshot Before, MemorySnapshot? After, string? ReadError);
    internal MemorySnapshot Read() => _api.Read();

    internal Result Clean()
    {
        using var gate = SystemMutationGate.Acquire();
        if (IsBusy) throw new InvalidOperationException(Str.T("Str.MemoryBusy"));
        if (!IsAdministrator) throw new UnauthorizedAccessException(Str.T("Str.MemoryAdmin"));
        var before = Read(); // A failed pre-read must not start a system operation.
        _api.PurgeLowPriorityStandby();
        try { return new(before, Read(), null); }
        catch (Exception ex) { return new(before, null, ex.Message); } // The purge already ran; keep that distinction.
    }
}

/// <summary>
/// Uses the low-priority standby command also used by Mem Reduct and MemListMgr.
/// API layout: winsiderss/phnt/ntexapi.h, SystemMemoryListInformation (80), command 5.
/// Independently implemented interop; no third-party source or resident process is bundled.
/// </summary>
internal sealed class WindowsMemoryCleanupApi : IMemoryCleanupApi
{
    private const uint MemoryListInformation = 80, PurgeLowPriorityStandbyCommand = 5;
    public MemorySnapshot Read()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Five scalar SIZE_Ts, eight standby counts, eight repurposed counts, one final scalar.
        var pages = new nuint[22];
        var status = NtQuerySystemInformation(MemoryListInformation, pages, (uint)(pages.Length * IntPtr.Size), out var length);
        ulong? low = status >= 0 && length >= 6 * IntPtr.Size
            ? checked((ulong)pages[5] * (ulong)Environment.SystemPageSize) : null;
        return new(memory.TotalPhysical, memory.AvailablePhysical, low);
    }

    public void PurgeLowPriorityStandby()
    {
        if (!OpenProcessToken(new IntPtr(-1), 0x20 | 0x8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref requested, (uint)Marshal.SizeOf<TokenPrivilege>(), out var previous, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = Marshal.GetLastWin32Error(); // A true return can still mean ERROR_NOT_ALL_ASSIGNED.
            if (error != 0) throw new Win32Exception(error);
            try
            {
                var command = PurgeLowPriorityStandbyCommand;
                var status = NtSetSystemInformation(MemoryListInformation, ref command, sizeof(uint));
                if (status < 0) throw new Win32Exception((int)RtlNtStatusToDosError(status), $"{Str.T("Str.MemoryNativeFailed")} (NTSTATUS 0x{status:X8})");
            }
            finally
            {
                if (previous.Count != 0)
                {
                    var restored = RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
                    var restoreError = Marshal.GetLastWin32Error();
                    if (!restored || restoreError != 0) throw new Win32Exception(restoreError);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(uint informationClass, [Out] nuint[] information, uint length, out uint returnLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(uint informationClass, ref uint command, uint length);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivilege state,
        uint length, out TokenPrivilege previous, out uint returnLength);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivilege state,
        uint length, IntPtr previous, IntPtr returnLength);
}
