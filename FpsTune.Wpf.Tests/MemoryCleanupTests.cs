using System.ComponentModel;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class MemoryCleanupTests
{
    internal sealed class FakeMemory : IMemoryCleanupApi
    {
        public int Reads, Purges;
        public int FailRead;
        public Exception? PurgeError;
        public ManualResetEventSlim? Hold;
        public bool AllPriorities;
        public bool KeepCache;
        public MemorySnapshot Read()
        {
            if (++Reads == FailRead) throw new InvalidOperationException("snapshot unavailable");
            return new(16UL << 30, 5UL << 30, Purges == 0 || KeepCache ? 300UL << 20 : 0, Purges == 0 ? 1UL << 30 : 0);
        }
        public void PurgeStandby(bool allPriorities)
        {
            AllPriorities = allPriorities;
            Purges++;
            if (Hold is not null && !Hold.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("test cleanup not released");
            if (PurgeError is not null) throw PurgeError;
        }
    }

    [Fact]
    public void Full_standby_mode_is_forwarded_once_without_trimming_process_working_sets()
    {
        var api = new FakeMemory();
        new MemoryCleanupService(api, () => false, () => true).Clean(allPriorities: true);
        Assert.True(api.AllPriorities); Assert.Equal(1, api.Purges);
    }

    [Fact]
    public void Cleanup_reads_before_and_after_without_fabricating_available_memory_gains()
    {
        var api = new FakeMemory();
        var result = new MemoryCleanupService(api, () => false, () => true).Clean();
        Assert.Equal(1, api.Purges); Assert.Equal(2, api.Reads);
        Assert.Equal(result.Before.Available, result.After!.Available);
        Assert.Equal(0UL, result.After.LowPriorityStandby); Assert.Null(result.ReadError);
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Active_capture_and_missing_elevation_do_not_read_or_purge(bool busy, bool admin)
    {
        var api = new FakeMemory();
        Assert.ThrowsAny<Exception>(() => new MemoryCleanupService(api, () => busy, () => admin).Clean());
        Assert.Equal(0, api.Reads); Assert.Equal(0, api.Purges);
    }
    [Fact]
    public void Failed_before_read_prevents_purge()
    {
        var api = new FakeMemory { FailRead = 1 };
        Assert.Throws<InvalidOperationException>(() => new MemoryCleanupService(api, () => false, () => true).Clean());
        Assert.Equal(0, api.Purges);
    }
    [Fact]
    public void Native_failure_is_not_reported_as_success_or_retried()
    {
        var api = new FakeMemory { PurgeError = new Win32Exception(1314) };
        Assert.Throws<Win32Exception>(() => new MemoryCleanupService(api, () => false, () => true).Clean());
        Assert.Equal(1, api.Purges); Assert.Equal(1, api.Reads);
    }
    [Fact]
    public void Failed_after_read_keeps_the_completed_operation_and_reports_unavailable_measurement()
    {
        var api = new FakeMemory { FailRead = 2 };
        var result = new MemoryCleanupService(api, () => false, () => true).Clean();
        Assert.Equal(1, api.Purges); Assert.Null(result.After); Assert.Equal("snapshot unavailable", result.ReadError);
    }
    [Fact]
    public void Windows_read_only_snapshot_has_real_physical_memory_and_valid_standby_layout()
    {
        var result = new WindowsMemoryCleanupApi().Read(); // No token privilege adjustment or purge.
        Assert.True(result.Total > 0); Assert.InRange(result.Available, 0UL, result.Total);
        Assert.NotNull(result.LowPriorityStandby); Assert.NotNull(result.Standby);
        Assert.InRange(result.LowPriorityStandby.Value, 0UL, result.Standby.Value); Assert.InRange(result.Standby.Value, 0UL, result.Total);
    }
    [Fact]
    public void Automatic_cleanup_is_opt_in_guarded_thresholded_and_observes_failure_cooldown()
    {
        using var gate = SystemMutationGate.Acquire();
        var api = new FakeMemory { KeepCache = true }; var busy = false; var admin = true;
        var options = new MemoryAutoCleanupOptions();
        using var automation = new MemoryAutoCleanupService(new(api, () => busy, () => admin), () => options);
        var now = DateTimeOffset.UtcNow;
        automation.Tick(now); Assert.Equal(0, api.Reads);
        options = new(true, 8192, 5);
        busy = true; automation.Tick(now); Assert.Equal(0, api.Reads);
        busy = false; admin = false; automation.Tick(now); Assert.Equal(0, api.Reads);
        admin = true; options = options with { ThresholdMiB = 1024 };
        automation.Tick(now); Assert.Equal(0, api.Purges);
        options = options with { ThresholdMiB = 8192 };
        api.PurgeError = new Win32Exception(1314);
        automation.Tick(now); automation.Tick(now.AddMinutes(1)); Assert.Equal(1, api.Purges);
        api.PurgeError = null; automation.Tick(now.AddMinutes(5)); Assert.Equal(2, api.Purges); Assert.False(api.AllPriorities);
    }
    [Fact]
    public void Combined_cleanup_reads_before_process_trim_and_preserves_per_process_failure_results()
    {
        var api = new FakeMemory(); var backgroundApi = new BackgroundApi();
        var background = new BackgroundMemoryService(backgroundApi, () => false);
        IReadOnlyList<MemoryProcessResult>? results = null;
        new MemoryCleanupService(api, () => false, () => true).Clean(true, () =>
        {
            Assert.Equal(1, api.Reads); Assert.Equal(0, api.Purges);
            results = background.Trim([new(10, 1, "ok", "ok.exe", 100), new(11, 1, "gone", "gone.exe", 100)]);
        });
        Assert.Equal(1, api.Purges); Assert.Equal(2, results!.Count); Assert.Null(results[0].Error); Assert.NotNull(results[1].Error);
        Assert.Throws<InvalidOperationException>(() => new BackgroundMemoryService(backgroundApi, () => true).Trim([]));
    }
    private sealed class BackgroundApi : IBackgroundMemoryApi
    {
        public IReadOnlyList<MemoryProcessTarget> Read() => [];
        public MemoryProcessResult Trim(MemoryProcessTarget target) => target.Pid == 10
            ? new(target.Name, 100, 30, null) : throw new InvalidOperationException("process exited");
    }
}
