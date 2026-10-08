using FpsTune.Wpf.Core;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class ExperimentAnchorTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Durable_anchor_is_required_before_first_system_write(bool anchorFails)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpstune-anchor-" + Guid.NewGuid().ToString("N"));
        var anchorExists = false; var writes = 0;
        BackupService.BackupDirOverride = dir;
        RegistryHelper.SnapshotOverride = (_, _, _) => new(true, 1, RegistryValueKind.DWord);
        RegistryHelper.SetOverride = (_, _, _, _, _) => { Assert.True(anchorExists); writes++; };
        try
        {
            var task = OptimizationEngine.ApplyItemsWithReceiptAsync(["transparency-off"], null, file =>
            {
                Assert.True(File.Exists(file));
                Assert.Empty(BackupService.ReadRecords(file));
                if (anchorFails) throw new IOException("Marker write failed");
                anchorExists = true;
            });
            if (anchorFails) { await Assert.ThrowsAsync<IOException>(() => task); Assert.Equal(0, writes); }
            else { await task; Assert.Equal(1, writes); }
        }
        finally
        {
            BackupService.BackupDirOverride = null; RegistryHelper.SnapshotOverride = null; RegistryHelper.SetOverride = null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
