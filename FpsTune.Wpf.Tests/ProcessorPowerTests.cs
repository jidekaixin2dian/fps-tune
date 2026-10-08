using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class ProcessorPowerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpstune-cpu-" + Guid.NewGuid().ToString("N"));
    private const string Plan = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public ProcessorPowerTests() => BackupService.BackupDirOverride = _dir;
    public void Dispose()
    {
        NativeSystem.RunOverride = null; NativePowerSettings.ReadOverride = null; BackupService.BackupDirOverride = null;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("cpu-epp-ac", false)] [InlineData("cpu-max-ac", false)]
    [InlineData("cpu-epp-ac", true)] [InlineData("cpu-max-ac", true)]
    public void Receipt_restores_exact_AC_target_and_preserves_external_change(string id, bool externalChange)
    {
        var option = PowerOption.For(id); var value = 50;
        var active = Plan; var writes = new List<string[]>();
        NativePowerSettings.ReadOverride = (plan, group, setting) =>
        {
            Assert.Equal(Plan, plan); Assert.Equal(PowerOption.ProcessorGroup, group); Assert.Equal(option.Setting, setting);
            return value;
        };
        NativeSystem.RunOverride = (_, args) =>
        {
            if (args[0] == "-getactivescheme") return new(0, active, "");
            writes.Add(args);
            Assert.Equal("-setacvalueindex", args[0]); Assert.Equal(Plan, args[1]);
            value = int.Parse(args[4]); return new(0, "", "");
        };
        var file = BackupService.Capture([], null);
        // The active plan can change after capture. Applying a setting must not switch it back.
        var results = NativeOptimizationEngine.ApplyAll([id], null,
            item => { var records = BackupService.AppendCapture(file, item, null); active = Guid.NewGuid().ToString("D"); return records; },
            result => BackupService.CompleteCapture(file, result));
        Assert.True(results[0].Ok, results[0].Message); Assert.Equal(option.Target, value);
        if (externalChange) value = 75;
        var restored = BackupService.RestoreAll(null, file);
        if (externalChange) { Assert.Single(restored.Failures); Assert.Single(writes); Assert.Equal(75, value); }
        else { Assert.Empty(restored.Failures); Assert.Equal(50, value); Assert.Equal(2, writes.Count); }
        Assert.False(ItemCatalog.All.Single(i => i.Id == id).Default);
    }

    [Fact]
    public void Unsupported_read_or_missing_original_prevents_write()
    {
        var writes = 0;
        NativeSystem.RunOverride = (_, args) => args[0] == "-getactivescheme" ? new(0, Plan, "") : new(++writes, "", "");
        NativePowerSettings.ReadOverride = (_, _, _) => throw new InvalidOperationException("Unsupported");
        Assert.Throws<InvalidOperationException>(() => BackupService.Capture(["cpu-epp-ac"], null));
        Assert.False(DetectionService.GetItemState(ItemCatalog.All.Single(item => item.Id == "cpu-epp-ac"), null).Optimized);
        Assert.Throws<InvalidOperationException>(() => BackupService.ValidateRecord(new() { Id="cpu-max-ac", Kind="power-setting", TargetPlanGuid=Plan }));
        Assert.Throws<InvalidOperationException>(() => PowerOption.For("unknown"));
        Assert.Equal(0, writes);
    }
}
