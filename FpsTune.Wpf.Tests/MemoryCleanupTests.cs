using System.ComponentModel;
using FpsTune.Wpf.Services;
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
        public MemorySnapshot Read()
        {
            if (++Reads == FailRead) throw new InvalidOperationException("snapshot unavailable");
            return new(16UL << 30, 5UL << 30, Purges == 0 ? 300UL << 20 : 0);
        }
        public void PurgeLowPriorityStandby()
        {
            Purges++;
            if (Hold is not null && !Hold.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("test cleanup not released");
            if (PurgeError is not null) throw PurgeError;
        }
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
        Assert.NotNull(result.LowPriorityStandby); Assert.InRange(result.LowPriorityStandby.Value, 0UL, result.Total);
    }
}
