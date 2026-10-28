using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record MemorySnapshot(ulong Total, ulong Available, ulong? LowPriorityStandby, ulong? Standby = null,
    ulong? Committed = null, ulong? CommitLimit = null);
internal interface IMemoryCleanupApi
{
    MemorySnapshot Read();
    void PurgeStandby(bool allPriorities);
}

internal sealed class MemoryCleanupService(IMemoryCleanupApi? api = null, Func<bool>? isBusy = null, Func<bool>? isAdministrator = null)
{
    private readonly IMemoryCleanupApi _api = api ?? new WindowsMemoryCleanupApi();
    internal bool IsAdministrator => (isAdministrator ?? AdminHelper.IsAdministrator)();
    internal bool IsBusy => (isBusy ?? (() => App.SessionService.IsRunning || ExperimentRunner.IsRunning))();
    internal sealed record Result(MemorySnapshot Before, MemorySnapshot? After, string? ReadError);
    internal MemorySnapshot Read() => _api.Read();

    internal MemoryCleanupRun Run(MemoryCleanupMode mode, IReadOnlyList<MemoryProcessTarget> targets,
        BackgroundMemoryService background, IProgress<MemoryCleanupProgress>? progress = null, CancellationToken cancellation = default)
    {
        using var gate = SystemMutationGate.Acquire(TimeSpan.Zero);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (IsBusy || GamePreparationService.IsRunning) throw new InvalidOperationException(Str.T("Str.MemoryBusy"));
        if (mode != MemoryCleanupMode.WorkingSets && !IsAdministrator)
            throw new UnauthorizedAccessException(Str.T("Str.MemoryAdmin"));
        if (mode is MemoryCleanupMode.WorkingSets or MemoryCleanupMode.Combined && targets.Count == 0)
            throw new InvalidOperationException(Str.T("Str.MemorySelectApps"));
        cancellation.ThrowIfCancellationRequested();
        var before = Read(); // Never write if the baseline cannot be measured.
        IReadOnlyList<MemoryProcessResult> apps = [];
        string? operationError = null, readError = null;
        var standbyCompleted = false;
        try
        {
            if (mode is MemoryCleanupMode.WorkingSets or MemoryCleanupMode.Combined)
                apps = background.Trim(targets, p => progress?.Report(p), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (IsBusy || GamePreparationService.IsRunning) throw new InvalidOperationException(Str.T("Str.MemoryBusy"));
            if (mode != MemoryCleanupMode.WorkingSets)
            {
                progress?.Report(new("standby", 0, 0));
                _api.PurgeStandby(mode != MemoryCleanupMode.LowStandby);
                standbyCompleted = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { operationError = ex.Message; }
        progress?.Report(new("measurement", apps.Count, targets.Count));
        MemorySnapshot? after = null;
        try { after = Read(); } catch (Exception ex) { readError = ex.Message; }
        return new(before, after, apps, standbyCompleted, operationError, readError, cancellation.IsCancellationRequested);
    }

    internal Result Clean(bool allPriorities = false, Action? prepare = null)
    {
        using var gate = SystemMutationGate.Acquire();
        if (IsBusy) throw new InvalidOperationException(Str.T("Str.MemoryBusy"));
        if (!IsAdministrator) throw new UnauthorizedAccessException(Str.T("Str.MemoryAdmin"));
        var before = Read(); // A failed pre-read must not start a system operation.
        prepare?.Invoke();
        _api.PurgeStandby(allPriorities);
        try { return new(before, Read(), null); }
        catch (Exception ex) { return new(before, null, ex.Message); } // The purge already ran; keep that distinction.
    }
}

/// <summary>
/// Uses the standby commands also used by Mem Reduct and MemListMgr.
/// API layout: winsiderss/phnt/ntexapi.h, SystemMemoryListInformation (80), commands 4 (all) and 5 (priority 0).
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
        ulong? low = status >= 0 && length >= pages.Length * IntPtr.Size
            ? checked((ulong)pages[5] * (ulong)Environment.SystemPageSize) : null;
        var standby = low is null ? (ulong?)null : checked(pages.Skip(5).Take(8).Aggregate(0UL, (sum, count) => checked(sum + (ulong)count)) * (ulong)Environment.SystemPageSize);
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        var measured = GetPerformanceInfo(ref performance, performance.Size);
        return new(measured ? (ulong)performance.PhysicalTotal * (ulong)performance.PageSize : memory.TotalPhysical,
            measured ? (ulong)performance.PhysicalAvailable * (ulong)performance.PageSize : memory.AvailablePhysical, low, standby,
            measured ? (ulong)performance.CommitTotal * (ulong)performance.PageSize : null,
            measured ? (ulong)performance.CommitLimit * (ulong)performance.PageSize : null);
    }

    public void PurgeStandby(bool allPriorities)
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
                var command = allPriorities ? 4u : PurgeLowPriorityStandbyCommand;
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
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);
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
