using FpsTune.Wpf.Core;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public class RecoveryPolicyTests
{
    [Fact]
    public void Nic_restore_policy_does_not_recapture_current_optimized_state()
    {
        var record = new BackupRecord { Id = "nic-power-save-off", Hive = "LocalMachine",
            Path = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0012",
            Name = "PnPCapabilities", ValueKind = "DWord", OldValue = 0, Existed = true };
        BackupService.ValidateRecord(record);
        record.Path = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0012";
        Assert.Throws<InvalidOperationException>(() => BackupService.ValidateRecord(record));
    }

    [Fact]
    public void Default_presets_exclude_manual_only_and_admin_items_from_safe_only()
    {
        Assert.All(OptimizationCatalog.ResolvePreset("balanced"), id => Assert.True(ItemCatalog.All.Single(i => i.Id == id).Default));
        Assert.All(OptimizationCatalog.ResolvePreset("safe-only"), id => Assert.False(ItemCatalog.All.Single(i => i.Id == id).Admin));
        Assert.True(ItemCatalog.All.Single(i => i.Id == "dvr-off").Admin);
    }
}
