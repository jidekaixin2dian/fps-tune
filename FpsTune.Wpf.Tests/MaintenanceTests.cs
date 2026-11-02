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
    public void Disk_health_is_read_only_and_reports_missing_detail_without_inventing_values()
    {
        DiskHealthService.ApiOverride = () => new FakeDiskHealth();
        try
        {
            var snapshot = DiskHealthService.Read();
            var disk = Assert.Single(snapshot.Disks);
            Assert.Equal("Fixture NVMe", disk.Name);
            Assert.Equal("NVMe · 正常", disk.Bus + " · " + disk.Health);
            Assert.False(disk.Detailed);
            Assert.Contains("DiskHealthDetailBoundary", Str.T("Str.DiskHealthDetailBoundary"));
            Assert.Equal("1 TB", DiskHealthService.FormatSize(1024L * 1024 * 1024 * 1024));
        }
        finally { DiskHealthService.ApiOverride = null; }
    }

    private sealed class FakeDiskHealth : IDiskHealthApi
    {
        public DiskHealthSnapshot Read() => new([
            new DiskHealthRow("Fixture NVMe", 1024L * 1024 * 1024 * 1024, "NVMe", "正常",
                "只提供基础健康状态", false)], "部分磁盘只提供基础健康状态。");
    }

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
