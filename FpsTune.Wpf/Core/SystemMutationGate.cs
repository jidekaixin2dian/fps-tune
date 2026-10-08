namespace FpsTune.Wpf.Core;

/// <summary>所有系统写入共享同一个跨进程互斥；仅在同一同步工作线程持有。</summary>
internal sealed class SystemMutationGate : IDisposable
{
    private readonly Mutex _mutex = new(false, BackupService.RestoreMutexName);
    private bool _held;
    private SystemMutationGate(TimeSpan timeout)
    {
        try { _held = _mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { _held = true; }
        if (!_held) { _mutex.Dispose(); throw new TimeoutException("Another system operation is running."); }
    }
    internal static SystemMutationGate Acquire() => new(TimeSpan.FromSeconds(30));
    internal static SystemMutationGate Acquire(TimeSpan timeout) => new(timeout);
    public void Dispose() { if (_held) { _held = false; _mutex.ReleaseMutex(); } _mutex.Dispose(); }
}
