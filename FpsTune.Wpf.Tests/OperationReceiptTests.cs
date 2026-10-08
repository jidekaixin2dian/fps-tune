using System.Text.Json.Nodes;
using FpsTune.Wpf.Core;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class OperationReceiptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpstune-receipt-" + Guid.NewGuid().ToString("N"));
    public OperationReceiptTests() { BackupService.BackupDirOverride = _dir; }
    public void Dispose()
    {
        RegistryHelper.SnapshotOverride = null; RegistryHelper.SetOverride = null; RegistryHelper.DeleteOverride = null;
        NativeSystem.RunOverride = null; NativePowerSettings.ReadOverride = null;
        NativePowerSettings.FingerprintOverride = null;
        BackupService.RestoreRecordOverride = null; BackupService.BackupDirOverride = null;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Partial_registry_failure_retains_anchor_and_uncertain_targets_that_can_be_restored()
    {
        var values = new Dictionary<string, object> { ["MouseSpeed"] = "1", ["MouseThreshold1"] = "6", ["MouseThreshold2"] = "10" };
        RegistryHelper.SnapshotOverride = (_, _, name) => new(values.ContainsKey(name), values.GetValueOrDefault(name), RegistryValueKind.String);
        var writes = 0;
        RegistryHelper.SetOverride = (_, _, name, value, _) => { if (++writes == 2) throw new IOException("injected write failure"); values[name] = value; };
        var run = await OptimizationEngine.ApplyItemsAsync(new[] { "mouse-accel-off" }, null);
        Assert.Equal(1, run.ExitCode);
        var json = JsonNode.Parse(run.Output)!;
        Assert.True(json["results"]![0]!["stateUncertain"]!.GetValue<bool>());
        var file = json["backupFile"]!.GetValue<string>();
        Assert.StartsWith("v2-backup-", Path.GetFileName(file));
        Assert.Equal("0", values["MouseSpeed"]);
        RegistryHelper.SetOverride = (_, _, name, value, _) => values[name] = value;
        var restored = BackupService.RestoreAll(new[] { "mouse-accel-off" }, file);
        Assert.Empty(restored.Failures);
        Assert.Equal("1", values["MouseSpeed"]);
        Assert.Equal("6", values["MouseThreshold1"]);
    }

    [Fact]
    public async Task Read_failure_happens_before_any_write()
    {
        RegistryHelper.SnapshotOverride = (_, _, _) => throw new IOException("injected read failure");
        var writes = 0;
        RegistryHelper.SetOverride = (_, _, _, _, _) => writes++;
        var run = await OptimizationEngine.ApplyItemsAsync(new[] { "transparency-off" }, null);
        Assert.Equal(1, run.ExitCode); Assert.Equal(0, writes);
        Assert.False(JsonNode.Parse(run.Output)!["results"]![0]!["stateUncertain"]!.GetValue<bool>());
    }

    [Fact]
    public void Unknown_legacy_power_target_does_not_block_known_registry_record()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "csharp-backup-mixed.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new BackupRecord { Id="pcie-aspm-off", Kind="power-aspm", OldAspmValue=2 },
            new BackupRecord { Id="transparency-off", Hive="CurrentUser", Path=@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name="EnableTransparency", OldValue=1, Existed=true }
        }));
        var restoredIds = new List<string>();
        BackupService.RestoreRecordOverride = record => restoredIds.Add(record.Id);
        var result = BackupService.RestoreAll();
        Assert.Equal(new[] { "transparency-off" }, restoredIds);
        Assert.Single(result.Failures); Assert.True(File.Exists(file));
        Assert.False(BackupService.ReadRecords(file)[0].Restored);
    }

    [Fact]
    public void Power_targets_follow_the_plan_created_by_the_previous_item_and_restore_in_reverse()
    {
        var original = "381b4222-f694-41f0-9685-ff5bb260df2e";
        var active = original;
        var index = 2;
        var commands = new List<string[]>();
        NativeSystem.RunOverride = (_, args) =>
        {
            commands.Add(args);
            if (args[0] == "-getactivescheme") return new(0, active, "");
            if (args[0] == "-setactive") active = args[1];
            if (args[0] == "-setacvalueindex") { Assert.Equal(active, args[1]); index = int.Parse(args[4]); }
            return new(0, "", "");
        };
        NativePowerSettings.ReadOverride = (_, _, _) => index;
        NativePowerSettings.FingerprintOverride = _ => index.ToString();
        var file = BackupService.Capture(Array.Empty<string>(), null);
        var result = NativeOptimizationEngine.ApplyAll(new[] { "power-ultimate", "pcie-aspm-off" }, null,
            id => BackupService.AppendCapture(file, id, null), item => BackupService.CompleteCapture(file, item));
        Assert.All(result, item => Assert.True(item.Ok, item.Message));
        var records = BackupService.ReadRecords(file);
        Assert.Equal(records[0].CreatedPlanGuid, records[1].TargetPlanGuid);
        Assert.NotEqual(original, active);
        var restored = BackupService.RestoreAll(null, file);
        Assert.Empty(restored.Failures);
        Assert.Equal(original, active); Assert.Equal(2, index);
        Assert.Contains(commands, args => args[0] == "-delete" && args[1] == records[0].CreatedPlanGuid);
        Assert.DoesNotContain(commands, args => args.Contains("SCHEME_CURRENT") || args.Contains("-attributes"));
    }

    [Fact]
    public async Task External_change_is_not_overwritten_on_restore()
    {
        var value = 1;
        RegistryHelper.SnapshotOverride = (_, _, _) => new(true, value, RegistryValueKind.DWord);
        RegistryHelper.SetOverride = (_, _, _, v, _) => value = Convert.ToInt32(v);
        var run = await OptimizationEngine.ApplyItemsAsync(new[] { "transparency-off" }, null);
        Assert.Equal(0, run.ExitCode);
        value = 2;
        var file = JsonNode.Parse(run.Output)!["backupFile"]!.GetValue<string>();
        var result = BackupService.RestoreAll(null, file);
        Assert.Single(result.Failures); Assert.Empty(result.Restored); Assert.Equal(2, value);
        Assert.True(File.Exists(file));
    }
}
