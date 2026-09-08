using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public class DeltaPreparationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"D:\Games\DeltaForceClient.exe", false)]
    [InlineData(@"D:\Games\cs2.exe", false)]
    [InlineData(@"D:\Games\DeltaForceClient-Win64-Shipping.exe", true)]
    [InlineData(@"D:\Games\DELTAFORCECLIENT-WIN64-SHIPPING.EXE", true)]
    public void Target_excludes_launchers_and_other_games(string? path, bool expected)
        => Assert.Equal(expected, DeltaPreparation.IsGameExecutable(path));

    [Fact]
    public void Advice_excludes_already_ready_and_advanced_items()
    {
        var items = new[] { Item("game-mode", true), Item("dvr-off"), Item("gpu-pref"), Item("sysmain-off"), Item("dynamic-tick-off") };
        Assert.Equal(new[] { "dvr-off", "gpu-pref" }, DeltaPreparation.PendingItems(items, true));
        Assert.Empty(DeltaPreparation.PendingItems(items, false));
        Assert.Empty(DeltaPreparation.PendingItems(new[] { Item("game-mode", true) }, true));
    }

    private static OptimizationItem Item(string id, bool ready = false)
        => new(id, id, "", "", false, false, ready, "", false, "系统");
}
