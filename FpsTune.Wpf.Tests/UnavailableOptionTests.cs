using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class UnavailableOptionTests
{
    [Fact]
    public void Unverified_memory_compression_write_is_skipped_before_capture_and_remains_restoreable()
    {
        var definition = ItemCatalog.All.Single(item => item.Id == "mem-compress-off");
        Assert.False(definition.Available);
        var results = NativeOptimizationEngine.ApplyAll([definition.Id], null,
            _ => throw new Exception("Must not capture or write an unavailable item."));
        Assert.True(results[0].Skipped); Assert.False(results[0].Changed);
        Assert.False(DetectionService.GetItemState(definition, null).Optimized);
        // Keeping the ID and target policy allows already-created backups to be restored.
        BackupService.ValidateRecord(new() { Id=definition.Id, Kind="registry", Hive="LocalMachine",
            Path=@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", Name="EnableCompression",
            Existed=false, ValueKind="DWord" });
    }
}
