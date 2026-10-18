using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class GamePreparationTests
{
    private static GamePreparationService Service(MemoryCleanupTests.FakeMemory api, bool busy = false, bool admin = true)
        => new(new MemoryCleanupService(api, () => busy, () => admin));
    private static GamePreparationOptions Combined => new() { Configured = true, ApplyIcc = true, IccPreset = IccFilterPreset.Warm,
        ApplyVibrance = true, VibrancePercent = 75, DisplayIdentity = "B", AllStandby = true };

    [Fact]
    public async Task Combined_actions_keep_the_selected_screen_and_existing_restore_points()
    {
        using var screens = new MultiDisplayColorTests(); var memory = new MemoryCleanupTests.FakeMemory();
        screens.BeforeIccWrite = () => screens.Displays = screens.Displays.Select(d => d with { IsPrimary = d.Identity == "B" }).ToList();
        var steps = await Service(memory).RunAsync(Combined);
        Assert.Equal(3, steps.Count); Assert.All(steps, s => Assert.True(s.Succeeded, s.Detail));
        Assert.Equal(1, memory.Purges); Assert.True(memory.AllPriorities);
        Assert.Equal(30, screens.Levels["A"]); Assert.Equal(75, screens.Levels["B"]);
        Assert.Equal(new[] { "a.icm" }, screens.Profiles["A"]);
        Assert.True(ScreenColorService.Restore("B")); Assert.True(DigitalVibranceService.Restore("B"));
        Assert.Equal(MultiDisplayColorTests.LinearRamp(), screens.Ramps["B"]);
        Assert.Equal(new[] { "b.icm" }, screens.Profiles["B"]); Assert.Equal(60, screens.Levels["B"]);
    }

    [Fact]
    public async Task Preparation_accepts_Windows_default_and_can_restore_it_without_a_custom_profile()
    {
        using var screens = new MultiDisplayColorTests();
        screens.Profiles["B"].Clear();
        var service = Service(new MemoryCleanupTests.FakeMemory());
        var options = Combined with { CleanMemory = false, ApplyVibrance = false };
        var applied = Assert.Single(await service.RunAsync(options));
        Assert.True(applied.Succeeded, applied.Detail);
        Assert.False(ScreenColorService.Matches(MultiDisplayColorTests.LinearRamp(), screens.Ramps["B"]));
        var restored = Assert.Single(await service.RunAsync(options with { IccPreset = null }));
        Assert.True(restored.Succeeded, restored.Detail);
        Assert.Empty(screens.Profiles["B"]);
        Assert.Equal(new[] { "a.icm" }, screens.Profiles["A"]);
        Assert.Empty(IccFilterService.BackupTargets());
        Assert.Empty(ScreenColorService.BackupTargets());
    }

    [Theory]
    [InlineData("busy")] [InlineData("admin")] [InlineData("unplugged")] [InlineData("invalid")]
    public async Task Failed_preflight_does_not_change_color_or_clean_memory(string reason)
    {
        using var screens = new MultiDisplayColorTests(); var memory = new MemoryCleanupTests.FakeMemory();
        var options = reason switch { "unplugged" => Combined with { DisplayIdentity = "missing" }, "invalid" => Combined with { VibrancePercent = 101 }, _ => Combined };
        await Assert.ThrowsAnyAsync<Exception>(() => Service(memory, reason == "busy", reason != "admin").RunAsync(options));
        Assert.Equal(0, memory.Purges); Assert.Empty(screens.Writes); Assert.False(GamePreparationService.IsRunning);
    }

    [Fact]
    public async Task Partial_failure_retains_successful_work_and_reports_each_step()
    {
        using var screens = new MultiDisplayColorTests { IgnoreVibWrite = true }; var memory = new MemoryCleanupTests.FakeMemory();
        var service = Service(memory); var steps = await service.RunAsync(Combined);
        Assert.True(steps[0].Succeeded); Assert.False(steps[1].Succeeded); Assert.True(steps[2].Succeeded);
        Assert.Equal(1, memory.Purges); Assert.Single(ScreenColorService.BackupTargets());
        Assert.True(DigitalVibranceService.HasRestorableBackup("B")); Assert.False(GamePreparationService.IsRunning);
        Assert.True((await service.RunAsync(new() { Configured = true })).Single().Succeeded);
    }

    [Fact]
    public async Task Active_preparation_blocks_duplicate_runs_and_recording_until_the_owned_work_finishes()
    {
        using var hold = new ManualResetEventSlim(); var memory = new MemoryCleanupTests.FakeMemory { Hold = hold };
        var service = Service(memory); var running = service.RunAsync(new() { Configured = true });
        try
        {
            Assert.True(GamePreparationService.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new()));
            using var recorder = new PerformanceSessionService();
            Assert.Throws<InvalidOperationException>(() => recorder.Start("must not start"));
            var experiment = await ExperimentRunner.RunAsync("baseline", new(), CancellationToken.None);
            Assert.Equal(1, experiment.ExitCode); Assert.Contains(Str.T("Str.PrepareBusy"), experiment.Json);
        }
        finally { hold.Set(); await running; }
        Assert.False(GamePreparationService.IsRunning); Assert.Equal(1, memory.Purges);
    }

    [Fact]
    public async Task A_failed_post_read_is_unconfirmed_and_does_not_claim_that_cleanup_did_not_run()
    {
        var memory = new MemoryCleanupTests.FakeMemory { FailRead = 2 };
        var result = Assert.Single(await Service(memory).RunAsync(new()));
        Assert.False(result.Succeeded); Assert.Contains("snapshot unavailable", result.Error); Assert.Equal(1, memory.Purges);
    }
}
