using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record MemoryAutoCleanupOptions(bool Enabled = false, int ThresholdMiB = 1024, int CooldownMinutes = 5)
{
    public MemoryAutoCleanupOptions Normalize() => this with
    { ThresholdMiB = Math.Clamp(ThresholdMiB, 256, 8192), CooldownMinutes = Math.Clamp(CooldownMinutes, 1, 60) };
}

/// <summary>App-lifetime, opt-in standby cleanup. Never trims applications automatically.</summary>
internal sealed class MemoryAutoCleanupService(MemoryCleanupService? memory = null, Func<MemoryAutoCleanupOptions>? options = null) : IDisposable
{
    private readonly MemoryCleanupService _memory = memory ?? new();
    private readonly Func<MemoryAutoCleanupOptions> _options = options ?? (() => SettingsService.Current.MemoryAutoCleanup);
    private Timer? _timer;
    private int _running;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    internal string? LastStatus { get; private set; }
    internal void Start() => _timer ??= new Timer(_ => Tick(DateTimeOffset.UtcNow), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    internal void Tick(DateTimeOffset now)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var policy = _options().Normalize();
            if (!policy.Enabled || _memory.IsBusy || GamePreparationService.IsRunning) return;
            if (!_memory.IsAdministrator) { LastStatus = Str.T("Str.MemoryAutoAdmin"); return; }
            if (now - _lastAttempt < TimeSpan.FromMinutes(policy.CooldownMinutes)) return;
            using var gate = SystemMutationGate.Acquire(TimeSpan.Zero);
            var snapshot = _memory.Read();
            if (snapshot.Available >= (ulong)policy.ThresholdMiB * 1024 * 1024
                || snapshot.LowPriorityStandby is not { } cache || cache < 128UL * 1024 * 1024) return;
            _lastAttempt = now; // Failed native operations also observe the cooldown.
            var result = _memory.Clean();
            LastStatus = result.After is { } after
                ? Str.T("Str.MemoryAutoDone", DateTime.Now.ToString("HH:mm:ss"),
                    $"{result.Before.Available / (1024d * 1024):F0}", $"{after.Available / (1024d * 1024):F0}")
                : Str.T("Str.MemoryCleanedReadFailed", result.ReadError);
        }
        catch (TimeoutException) { /* Another mutation owns the gate; try at the next interval. */ }
        catch (Exception ex) { _lastAttempt = now; LastStatus = Str.T("Str.MemoryFailed", PrivacyScrub.Sanitize(ex.Message)); }
        finally { Volatile.Write(ref _running, 0); }
    }
    public void Dispose() => _timer?.Dispose();
}
