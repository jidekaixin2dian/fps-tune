using FpsTune.Wpf.Core;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class HibernateDetectionTests : IDisposable
{
    public void Dispose() => RegistryHelper.SnapshotOverride = null;
    private static (bool Optimized, string Current) Read()
        => DetectionService.GetItemState(ItemCatalog.All.Single(item => item.Id == "hibernate-off"), null);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Known_state_remains_exact(int value, bool optimized)
    {
        RegistryHelper.SnapshotOverride = (_, _, _) => new(true, value, RegistryValueKind.DWord);
        Assert.Equal(optimized, Read().Optimized);
        Assert.Equal(value == 1, NativeSystem.IsHibernateEnabled());
    }

    [Theory]
    [InlineData(false, 0, RegistryValueKind.None)]
    [InlineData(true, 0, RegistryValueKind.String)]
    [InlineData(true, 3, RegistryValueKind.DWord)]
    public void Missing_or_invalid_state_does_not_abort_detection_or_weaken_capture(bool exists, int value, RegistryValueKind kind)
    {
        RegistryHelper.SnapshotOverride = (_, _, _) => new(exists, value, kind);
        var state = Read();
        Assert.False(state.Optimized);
        Assert.False(string.IsNullOrWhiteSpace(state.Current));
        Assert.Throws<InvalidOperationException>(() => NativeSystem.IsHibernateEnabled());
    }

    [Fact]
    public void Read_permission_failure_is_reported_but_still_blocks_capture()
    {
        RegistryHelper.SnapshotOverride = (_, _, _) => throw new UnauthorizedAccessException("Denied");
        Assert.False(Read().Optimized);
        Assert.Throws<UnauthorizedAccessException>(() => NativeSystem.IsHibernateEnabled());
    }
}
