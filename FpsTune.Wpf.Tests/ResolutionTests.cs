using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json.Nodes;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ResolutionTests
{
    internal sealed class Fixture : IDisposable, IDisplayModeApi, INvidiaDisplayModes
    {
        internal static readonly DisplayMode Native = new(2560, 1440, 240);
        internal IccDisplayInfo Display = new("display-1", 1, 0, 0, "0001", true, "monitor-A", "Test screen");
        internal IccDisplayInfo Second = new("display-2", 1, 0, 1, "0002", true, "monitor-B", "Second screen", false);
        internal DisplayMode Current = Native;
        internal DisplayMode OtherCurrent = Native;
        internal uint Scale = 5;
        internal bool Connected = true, FailApply, FailGuard, RejectCustom;
        internal int Trials, Saves, Reverts, Writes, Validations;
        internal int UnsettledReads;
        internal bool CustomKeepsWindowsMode, CustomChangesColor, IgnoreScaling;
        internal string Root = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "resolution-20261024", "tests", Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Directory.CreateDirectory(Root); UserDataPaths.RootOverride = Root;
            DisplayTopology.Override = () => Connected ? [Display, Second] : [Second];
            ResolutionService.ApiOverride = this; ResolutionService.NvidiaOverride = this;
            ResolutionService.GuardOverride = _ => { if (FailGuard) throw new InvalidOperationException("guard refused"); };
        }
        public DisplayMode Read(IccDisplayInfo display)
        {
            if (UnsettledReads > 0) { UnsettledReads--; throw new InvalidOperationException("display is rebuilding"); }
            return display.Identity == Display.Identity ? Current : OtherCurrent;
        }
        public IReadOnlyList<DisplayMode> Modes(IccDisplayInfo display) => [Current, Native, new(1024, 768, 240), new(1280, 960, 240), new(1280, 960, 144)];
        public void Test(IccDisplayInfo display, DisplayMode mode) { mode.Validate(); Validations++; }
        public void Apply(IccDisplayInfo display, DisplayMode mode)
        { Assert.Equal(Display.Identity, display.Identity); if (FailApply) { FailApply = false; throw new InvalidOperationException("driver refused"); } Current = mode; Writes++; }
        public void ProbeCustom(IccDisplayInfo display, DisplayMode mode) { if (RejectCustom) throw new InvalidOperationException("custom timing unavailable"); }
        public void TryCustom(IccDisplayInfo display, DisplayMode mode)
        {
            Trials++; if (!CustomKeepsWindowsMode) Current = mode; UnsettledReads = 2;
            if (CustomChangesColor) Display = Display with { AdvancedColorEnabled = false, ColorMode = DisplayColorMode.Sdr };
        }
        public void SaveCustom(IccDisplayInfo display) { Saves++; }
        public void RevertTrial(IccDisplayInfo display) { Reverts++; Current = Native; }
        public uint ReadScaling(IccDisplayInfo display) => Scale;
        public void SetScaling(IccDisplayInfo display, uint scaling, bool testOnly = false)
        { if (!testOnly) { if (!IgnoreScaling) Scale = scaling; Writes++; } }
        internal string Begin(int w, int h, int hz = 240, uint? scale = null) => ResolutionService.Begin("monitor-A", ResolutionService.Plan("monitor-A", w, h, hz, scale));
        internal string Receipt => Directory.GetFiles(ResolutionService.DirectoryPath, "resolution-*.json").Single();
        public void Dispose()
        {
            DisplayTopology.Override = null; ResolutionService.ApiOverride = null; ResolutionService.NvidiaOverride = null;
            ResolutionService.GuardOverride = null; UserDataPaths.RootOverride = null;
        }
    }
    [Fact]
    public void Existing_modes_never_create_custom_modes_and_repeated_trials_keep_the_first_restore_point()
    {
        using var f = new Fixture();
        var token = f.Begin(1024, 768, scale: 2); ResolutionService.Confirm("monitor-A", token);
        token = f.Begin(1280, 960); ResolutionService.Confirm("monitor-A", token);
        Assert.Equal(0, f.Trials); Assert.Equal(0, f.Saves);
        Assert.True(ResolutionService.Restore("monitor-A")); Assert.Equal(Fixture.Native, f.Current); Assert.Equal(5u, f.Scale);
        Assert.Empty(ResolutionService.BackupTargets());
    }
    [Fact]
    public void Custom_modes_are_saved_only_on_confirmation_and_unconfirmed_modes_revert()
    {
        using var f = new Fixture();
        var token = f.Begin(1440, 1080);
        Assert.Equal(1, f.Trials); Assert.Equal(0, f.Saves);
        Assert.Throws<InvalidOperationException>(() => ResolutionService.Confirm("monitor-A", Guid.NewGuid().ToString("N")));
        ResolutionService.Restore("monitor-A"); Assert.Equal(1, f.Reverts); Assert.Equal(Fixture.Native, f.Current);
        token = f.Begin(1920, 1440); ResolutionService.Confirm("monitor-A", token);
        Assert.Equal(1, f.Saves); ResolutionService.Restore("monitor-A"); Assert.Equal(Fixture.Native, f.Current);
    }
    [Fact]
    public void Custom_trial_waits_for_mode_rebuild_and_reverts_even_if_Windows_already_restored_itself()
    {
        using var f = new Fixture(); var token = f.Begin(1440, 1080);
        Assert.True(ResolutionService.TrialState("monitor-A").CanConfirm);
        f.Current = Fixture.Native;
        Assert.True(ResolutionService.RestoreTrial("monitor-A", token));
        Assert.Equal(1, f.Reverts); Assert.Empty(ResolutionService.BackupTargets());
        f.FailGuard = true;
        Assert.Throws<InvalidOperationException>(() => f.Begin(1440, 1080));
        Assert.Equal(1, f.Reverts); // No native trial was attempted after the failed guard startup.
    }
    [Fact]
    public void A_custom_trial_that_never_reaches_the_requested_mode_is_reverted_without_saving()
    {
        using var f = new Fixture(); f.CustomKeepsWindowsMode = true;
        Assert.Throws<InvalidOperationException>(() => f.Begin(1440, 1080));
        Assert.Equal(1, f.Reverts); Assert.Equal(0, f.Saves);
        Assert.Empty(ResolutionService.BackupTargets()); Assert.Equal(Fixture.Native, f.Current);
    }
    [Fact]
    public void Resolution_accepts_a_mode_induced_color_change_without_relaxing_color_tool_guards()
    {
        using var f = new Fixture();
        f.Display = f.Display with { AdvancedColorEnabled = true, ColorMode = DisplayColorMode.Wcg };
        var captured = f.Display; f.CustomChangesColor = true;
        var token = f.Begin(1440, 1080);
        Assert.Throws<InvalidOperationException>(() => DisplayTopology.ValidateConnection(captured));
        Assert.Equal(f.Display, DisplayTopology.ValidatePhysicalConnection(captured));
        ResolutionService.Confirm("monitor-A", token); Assert.Equal(1, f.Saves);
        ResolutionService.Restore("monitor-A"); Assert.Equal(Fixture.Native, f.Current);
    }
    [Fact]
    public void A_driver_ignoring_scaling_is_not_confirmed_and_the_original_desktop_is_restored()
    {
        using var f = new Fixture(); f.IgnoreScaling = true;
        Assert.Throws<InvalidOperationException>(() => f.Begin(1280, 960, scale: 2));
        Assert.Equal(Fixture.Native, f.Current); Assert.Equal(5u, f.Scale);
        Assert.Empty(ResolutionService.BackupTargets());
        Assert.Equal(ResolutionTrialPhase.Reverted, ResolutionService.TrialState("monitor-A").Phase);
    }
    [Fact]
    public void Hotplug_identity_reuse_external_modes_and_scaling_preserve_receipts_without_writes()
    {
        using var f = new Fixture(); var token = f.Begin(1024, 768, scale: 2); ResolutionService.Confirm("monitor-A", token);
        var writes = f.Writes;
        f.Connected = false; Assert.Throws<InvalidOperationException>(() => ResolutionService.Restore("monitor-A"));
        f.Connected = true; f.Display = f.Display with { SourceId = 4 }; Assert.Throws<InvalidOperationException>(() => ResolutionService.Restore("monitor-A"));
        f.Display = f.Display with { SourceId = 0 }; var applied = f.Current;
        f.Current = new(1920, 1200, 240); Assert.Throws<InvalidOperationException>(() => ResolutionService.Restore("monitor-A"));
        f.Current = applied; f.Scale = 3; Assert.Throws<InvalidOperationException>(() => ResolutionService.Restore("monitor-A"));
        Assert.Equal(writes, f.Writes); Assert.True(File.Exists(f.Receipt));
        f.Scale = 2; ResolutionService.Restore("monitor-A"); Assert.Equal(Fixture.Native, f.Current);
    }
    [Fact]
    public void Guard_start_failure_and_driver_rejection_never_leave_an_unprotected_trial()
    {
        using var f = new Fixture(); f.FailGuard = true;
        Assert.Throws<InvalidOperationException>(() => f.Begin(1024, 768)); Assert.Equal(0, f.Writes); Assert.Empty(ResolutionService.BackupTargets());
        f.FailGuard = false; f.FailApply = true;
        Assert.Throws<InvalidOperationException>(() => f.Begin(1024, 768)); Assert.Equal(Fixture.Native, f.Current); Assert.Empty(ResolutionService.BackupTargets());
    }
    [Fact]
    public void Expired_guard_rolls_back_without_the_UI_and_expired_confirmation_is_rejected()
    {
        using var f = new Fixture(); var token = f.Begin(1024, 768);
        var json = JsonNode.Parse(File.ReadAllText(f.Receipt))!; json["Deadline"] = DateTimeOffset.UtcNow.AddSeconds(-1);
        File.WriteAllText(f.Receipt, json.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => ResolutionService.Confirm("monitor-A", token));
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\FpsTune.Resolution." + token);
        Assert.Equal(0, ResolutionService.RunGuard(token)); Assert.True(ready.WaitOne(0));
        Assert.Equal(Fixture.Native, f.Current); Assert.Empty(ResolutionService.BackupTargets());
    }
    [Fact]
    public void Transfer_rejects_pending_or_misnamed_receipts_and_includes_confirmed_resolution_backups()
    {
        using var f = new Fixture(); var token = f.Begin(1024, 768);
        Assert.Throws<InvalidDataException>(() => ResolutionService.ValidateTransferredBackup(Path.GetFileName(f.Receipt), File.ReadAllBytes(f.Receipt)));
        ResolutionService.Confirm("monitor-A", token);
        ResolutionService.ValidateTransferredBackup(Path.GetFileName(f.Receipt), File.ReadAllBytes(f.Receipt));
        Assert.Throws<InvalidDataException>(() => ResolutionService.ValidateTransferredBackup("resolution-wrong.json", File.ReadAllBytes(f.Receipt)));
        Assert.Contains(RecoveryCoordinator.Inventory(), s => s.Module == "display-resolution" && s.Target == "Test screen");
        var zip = Path.Combine(f.Root, "backup.zip"); FpsTune.Wpf.Core.BackupService.ExportBackups(zip);
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName.StartsWith("display-resolution/resolution-"));
        ResolutionService.Restore("monitor-A");
    }
    [Fact]
    public void Native_ABI_sizes_match_public_NVAPI_and_Windows_headers()
    {
        Assert.Equal(220, Marshal.SizeOf<WindowsDisplayModeApi.DevMode>());
        Assert.Equal(96, Marshal.SizeOf<NvidiaDisplayModes.Timing>());
        Assert.Equal(32, Marshal.SizeOf<NvidiaDisplayModes.TimingInput>());
        Assert.Equal(144, Marshal.SizeOf<NvidiaDisplayModes.CustomDisplay>());
        Assert.Equal(44, Marshal.OffsetOf<NvidiaDisplayModes.CustomDisplay>(nameof(NvidiaDisplayModes.CustomDisplay.Timing)).ToInt32());
    }
    [Fact]
    public void Stopped_or_reused_guard_identity_blocks_confirmation_but_preserves_manual_recovery()
    {
        using var f = new Fixture(); var token = f.Begin(1024, 768);
        var status = Directory.GetFiles(ResolutionService.DirectoryPath, "trial-*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(status))!;
        using var current = Process.GetCurrentProcess();
        json["GuardPid"] = current.Id; json["GuardStartedTicks"] = current.StartTime.ToUniversalTime().Ticks;
        json["GuardPath"] = Environment.ProcessPath;
        File.WriteAllText(status, json.ToJsonString()); ResolutionService.GuardOverride = null;
        Assert.True(ResolutionService.TrialState("monitor-A").CanConfirm);
        json["GuardStartedTicks"] = 1; File.WriteAllText(status, json.ToJsonString());
        var state = ResolutionService.TrialState("monitor-A");
        Assert.True(state.Pending); Assert.False(state.CanConfirm); Assert.Equal(ResolutionTrialPhase.Failed, state.Phase);
        Assert.Throws<InvalidOperationException>(() => ResolutionService.Confirm("monitor-A", token));
        var writes = f.Writes; f.Connected = false;
        Assert.Equal(token, ResolutionService.TrialState("monitor-A").Token);
        Assert.Throws<InvalidOperationException>(() => ResolutionService.RestoreTrial("monitor-A", token));
        Assert.Equal(writes, f.Writes); f.Connected = true;
        Assert.True(ResolutionService.RestoreTrial("monitor-A", token)); Assert.Equal(Fixture.Native, f.Current);
        Assert.Equal(ResolutionTrialPhase.Reverted, ResolutionService.TrialState("monitor-A").Phase);
    }
    [Fact]
    public void Bounded_guard_failure_is_terminal_and_an_unrelated_bad_receipt_does_not_disable_protection()
    {
        using var f = new Fixture(); var token = f.Begin(1024, 768); var receipt = f.Receipt;
        var json = JsonNode.Parse(File.ReadAllText(receipt))!; json["Deadline"] = DateTimeOffset.UtcNow.AddSeconds(-1);
        File.WriteAllText(receipt, json.ToJsonString());
        File.WriteAllText(Path.Combine(ResolutionService.DirectoryPath, "resolution-" + new string('A', 64) + ".json"), "{broken");
        f.Connected = false; var writes = f.Writes;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\FpsTune.Resolution." + token);
        Assert.Equal(1, ResolutionService.RunGuard(token, attempts: 1));
        var state = ResolutionService.TrialState("monitor-A");
        Assert.Equal(ResolutionTrialPhase.Failed, state.Phase); Assert.False(state.CanConfirm); Assert.NotNull(state.Error);
        Assert.Equal(writes, f.Writes); Assert.True(File.Exists(receipt));
        f.Connected = true; Assert.True(ResolutionService.RestoreTrial("monitor-A", token)); Assert.Equal(Fixture.Native, f.Current);
    }
    [Fact]
    public void Stale_cancel_cannot_restore_a_new_trial_and_missing_receipt_never_reports_success()
    {
        using var f = new Fixture(); var old = f.Begin(1024, 768); ResolutionService.RestoreTrial("monitor-A", old);
        var token = f.Begin(1280, 960); var writes = f.Writes;
        Assert.False(ResolutionService.RestoreTrial("monitor-A", old)); Assert.Equal(writes, f.Writes);
        Assert.Throws<InvalidOperationException>(() => ResolutionService.Begin("monitor-B", ResolutionService.Plan("monitor-B", 1024, 768, 240, null)));
        Assert.Equal(writes, f.Writes);
        File.Delete(f.Receipt);
        Assert.Equal(ResolutionTrialPhase.Unknown, ResolutionService.TrialState("monitor-A").Phase);
        Assert.Equal(token, ResolutionService.TrialState("monitor-A").Token);
    }
}
