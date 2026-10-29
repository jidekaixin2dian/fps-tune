using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class PreparationMemoryIntegrationTests
{
    [Fact]
    public async Task Full_preparation_reuses_working_sets_and_all_standby_but_legacy_plans_do_not_expand()
    {
        var memory = new MemoryCleanupTests.FakeMemory(); var apps = new Apps();
        var service = new GamePreparationService(new(memory, () => false, () => true), new(apps));
        var progress = new List<string>();
        var step = Assert.Single(await service.RunAsync(new() { Configured = true, FullMemoryCleanup = true },
            memoryProgress: new ProgressSink(p => progress.Add(p.Stage))));
        Assert.True(step.Succeeded, step.Detail); Assert.Equal(1, apps.Reads); Assert.Equal(2, apps.Trimmed);
        Assert.True(memory.AllPriorities); Assert.Equal(1, memory.Purges);
        Assert.Contains("discovery", progress); Assert.Contains("apps", progress); Assert.Contains("standby", progress); Assert.Contains("measurement", progress);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<GamePreparationOptions>("{\"Configured\":true,\"AllStandby\":false}")!;
        Assert.False(legacy.FullMemoryCleanup); Assert.True(Assert.Single(await service.RunAsync(legacy)).Succeeded);
        Assert.Equal(1, apps.Reads); Assert.Equal(2, apps.Trimmed); Assert.False(memory.AllPriorities); Assert.False(GamePreparationService.IsRunning);
    }
    [Fact]
    public async Task Owned_cleanup_remains_exclusive_and_stop_preserves_completed_work_without_purging()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var memory = new MemoryCleanupTests.FakeMemory(); var apps = new Apps { OnTrim = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var service = new GamePreparationService(new(memory, () => false, () => true), new(apps));
        var run = service.RunAsync(new() { FullMemoryCleanup = true }, cancellation: stop.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3))); Assert.True(GamePreparationService.BlocksMemoryCleanup);
            Assert.Throws<TimeoutException>(() => new MemoryCleanupService(memory, () => false, () => true)
                .Run(MemoryCleanupMode.Full, [], new(apps)));
            stop.Cancel();
        }
        finally { release.Set(); }
        var result = Assert.Single(await run); Assert.False(result.Succeeded); Assert.Equal(1, apps.Trimmed); Assert.Equal(0, memory.Purges);
        Assert.False(GamePreparationService.IsRunning); Assert.False(GamePreparationService.BlocksMemoryCleanup);
        Assert.True(Assert.Single(await service.RunAsync(new())).Succeeded); Assert.Equal(1, memory.Purges);
    }
    [Fact]
    public async Task Full_cleanup_reports_per_target_failure_and_does_not_write_after_failed_measurement_or_discovery()
    {
        var memory = new MemoryCleanupTests.FakeMemory(); var apps = new Apps { FailOne = true };
        var service = new GamePreparationService(new(memory, () => false, () => true), new(apps));
        var partial = Assert.Single(await service.RunAsync(new() { FullMemoryCleanup = true }));
        Assert.False(partial.Succeeded); Assert.Contains("protected fixture", partial.Error); Assert.Equal(1, memory.Purges);
        memory = new() { FailRead = 1 }; apps = new();
        service = new(new(memory, () => false, () => true), new(apps));
        Assert.False(Assert.Single(await service.RunAsync(new() { FullMemoryCleanup = true })).Succeeded);
        Assert.Equal(0, memory.Purges); Assert.Equal(0, apps.Trimmed);
        apps.ThrowRead = true; memory = new(); service = new(new(memory, () => false, () => true), new(apps));
        Assert.False(Assert.Single(await service.RunAsync(new() { FullMemoryCleanup = true })).Succeeded);
        Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Purges);
    }
    internal sealed class Apps : IBackgroundMemoryApi
    {
        public int Reads, Trimmed; public bool FailOne, ThrowRead; public Action? OnTrim;
        public IReadOnlyList<MemoryProcessTarget> Read()
        {
            Reads++; if (ThrowRead) throw new InvalidOperationException("discovery unavailable");
            return [new(10001, 1, "background-a", "background-a.exe", 100), new(10002, 2, "background-b", "background-b.exe", 100)];
        }
        public MemoryProcessResult Trim(MemoryProcessTarget target)
        {
            Trimmed++; OnTrim?.Invoke();
            return new(target.Name, 100, FailOne && target.Pid == 10002 ? null : 50, FailOne && target.Pid == 10002 ? "protected fixture" : null);
        }
    }
    private sealed class ProgressSink(Action<MemoryCleanupProgress> report) : IProgress<MemoryCleanupProgress>
    { public void Report(MemoryCleanupProgress value) => report(value); }
}
