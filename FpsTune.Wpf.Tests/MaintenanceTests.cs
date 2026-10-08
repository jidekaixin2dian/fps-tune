using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public class MaintenanceTests
{
    [Theory]
    [InlineData(100, 20, 80, false)]
    [InlineData(100, 9, 91, true)]
    [InlineData(100, 10, 90, false)]
    [InlineData(0, 0, 0, false)]
    [InlineData(100, 150, 0, false)]
    public void Capacity_calculation_handles_empty_and_unusual_drives(long total, long available, double used, bool low)
    {
        var drive = new MaintenanceService.DriveSnapshot("C:\\", total, available);
        Assert.Equal(used, drive.UsedPercent);
        Assert.Equal(low, drive.LowSpace);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("reliability /report")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("../Taskmgr.exe")]
    public void Tool_launcher_rejects_unlisted_targets(string id)
        => Assert.Throws<ArgumentException>(() => MaintenanceService.CreateLaunch(id, @"C:\Windows\System32"));

    [Fact]
    public void Built_in_tools_do_not_resolve_from_the_working_directory_or_PATH()
    {
        var start = MaintenanceService.CreateLaunch("reliability", @"C:\Windows\System32");
        Assert.Equal(@"C:\Windows\System32\perfmon.exe", start.FileName);
        Assert.Equal("/rel", start.Arguments);
        Assert.True(start.UseShellExecute);
        Assert.Throws<ArgumentException>(() => MaintenanceService.CreateLaunch("taskmanager", "relative"));
    }
}
