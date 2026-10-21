using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record MemoryProcessTarget(int Pid, long Started, string Name, string Path, long WorkingSet);
internal sealed record MemoryProcessResult(string Name, long Before, long? After, string? Error);
internal interface IBackgroundMemoryApi
{
    IReadOnlyList<MemoryProcessTarget> Read();
    MemoryProcessResult Trim(MemoryProcessTarget target);
}

internal sealed class BackgroundMemoryService(IBackgroundMemoryApi? api = null, Func<bool>? isBusy = null)
{
    private readonly IBackgroundMemoryApi _api = api ?? new WindowsBackgroundMemoryApi();
    internal IReadOnlyList<MemoryProcessTarget> Read() => _api.Read();
    internal IReadOnlyList<MemoryProcessResult> Trim(IEnumerable<MemoryProcessTarget> targets)
    {
        using var gate = SystemMutationGate.Acquire(TimeSpan.Zero);
        if ((isBusy ?? (() => App.SessionService.IsRunning || ExperimentRunner.IsRunning || GamePreparationService.IsRunning))())
            throw new InvalidOperationException(Str.T("Str.MemoryBusy"));
        return targets.DistinctBy(t => t.Pid).Take(200).Select(target =>
        {
            try { return _api.Trim(target); }
            catch (Exception ex) { return new MemoryProcessResult(target.Name, target.WorkingSet, null, ex.Message); }
        }).ToArray();
    }
}

internal sealed class WindowsBackgroundMemoryApi : IBackgroundMemoryApi
{
    private static HashSet<string> Excluded()
        => StateStore.LoadGames().Select(g => Path.GetFileName(g.ExePath)).Append(Path.GetFileName(AppState.GamePath ?? ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static bool ExcludedName(string name, HashSet<string> excluded)
        => GamePathService.IsKnownGameExecutable(name) || excluded.Contains(name)
            || name.Contains("anticheat", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ACE-", StringComparison.OrdinalIgnoreCase);
    private static bool SameUser(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token)) return false;
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent()) return owner.User == current.User;
    }
    private static SafeProcessHandle Open(int pid, bool trim)
    {
        var handle = OpenProcess(0x1000u | (trim ? 0x100u : 0u), false, pid);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        if (!IsProcessCritical(handle, out var critical) || critical || !SameUser(handle))
        { handle.Dispose(); throw new InvalidOperationException(Str.T("Str.MemoryProcessProtected")); }
        return handle;
    }
    private static string ImagePath(SafeProcessHandle process)
    {
        var path = new System.Text.StringBuilder(32768); var length = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }
    private static long Started(SafeProcessHandle process)
    {
        if (!GetProcessTimes(process, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return DateTime.FromFileTimeUtc(created).Ticks;
    }
    public IReadOnlyList<MemoryProcessTarget> Read()
    {
        var result = new List<MemoryProcessTarget>(); var excluded = Excluded();
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.SessionId != current.SessionId || ExcludedName(process.ProcessName + ".exe", excluded)) continue;
                using var handle = Open(process.Id, false); var path = ImagePath(handle);
                if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var bytes = process.WorkingSet64;
                if (bytes >= 16L * 1024 * 1024) result.Add(new(process.Id, Started(handle), process.ProcessName, path, bytes));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return result.OrderByDescending(p => p.WorkingSet).Take(200).ToArray();
    }
    public MemoryProcessResult Trim(MemoryProcessTarget target)
    {
        var excluded = Excluded();
        if (target.Pid == Environment.ProcessId || ExcludedName(Path.GetFileName(target.Path), excluded))
            throw new InvalidOperationException(Str.T("Str.MemoryProcessProtected"));
        using var process = Process.GetProcessById(target.Pid);
        using var current = Process.GetCurrentProcess();
        using var handle = Open(target.Pid, true);
        var path = ImagePath(handle);
        if (process.SessionId != current.SessionId || Started(handle) != target.Started
            || !path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Str.T("Str.MemoryProcessChanged"));
        process.Refresh(); var before = process.WorkingSet64;
        if (!EmptyWorkingSet(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { process.Refresh(); return new(target.Name, before, process.WorkingSet64, null); }
        catch (Exception ex) { return new(target.Name, before, null, Str.T("Str.MemoryProcessReadFailed", ex.Message)); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, System.Text.StringBuilder path, ref int size);
    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
