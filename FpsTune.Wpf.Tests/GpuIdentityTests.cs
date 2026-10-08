using FpsTune.Wpf.Core;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class GpuIdentityTests : IDisposable
{
    private const string Instance = @"PCI\VEN_10DE&DEV_2484&SUBSYS_000010DE&REV_A1\4&123456&0&0008";
    private const string PathPrefix = @"SYSTEM\CurrentControlSet\Enum\";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpstune-gpu-tests-" + Guid.NewGuid().ToString("N"));
    private string _description = "@oem1.inf,%gpu%;NVIDIA GeForce RTX 3070";
    private string _driverVersion = "1.0";
    private int _writes;
    private bool _failWrite;

    public GpuIdentityTests()
    {
        BackupService.BackupDirOverride = _dir;
        GpuIdentityService.AdminCheckOverride = () => true;
        GpuIdentityService.DeviceIdsOverride = () => new[] { Instance };
        RegistryHelper.SnapshotOverride = (_, _, name) => name switch
        {
            "ClassGUID" => new(true, "{4d36e968-e325-11ce-bfc1-08002be10318}", RegistryValueKind.String),
            "Driver" => new(true, @"{4d36e968-e325-11ce-bfc1-08002be10318}\0000", RegistryValueKind.String),
            "HardwareID" => new(true, new[] { @"PCI\VEN_10DE&DEV_2484" }, RegistryValueKind.MultiString),
            "DriverVersion" => new(true, _driverVersion, RegistryValueKind.String),
            "DriverDesc" => new(true, "NVIDIA GeForce RTX 3070", RegistryValueKind.String),
            "DeviceDesc" => new(true, _description, RegistryValueKind.String),
            _ => new(false, null, RegistryValueKind.Unknown)
        };
        RegistryHelper.SetOverride = (_, path, name, value, kind) =>
        {
            Assert.Equal(PathPrefix + Instance, path);
            Assert.Equal("DeviceDesc", name);
            Assert.Equal(RegistryValueKind.String, kind);
            Assert.Single(BackupService.ListBackups()); // 每一次写入之前均已持久化原值
            _writes++;
            if (_failWrite) throw new UnauthorizedAccessException("injected permission denial");
            _description = Assert.IsType<string>(value);
        };
    }

    public void Dispose()
    {
        GpuIdentityService.DeviceIdsOverride = null; GpuIdentityService.AdminCheckOverride = null;
        RegistryHelper.SnapshotOverride = null; RegistryHelper.SetOverride = null;
        BackupService.BackupDirOverride = null;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Name_change_has_durable_backup_and_lossless_restore()
    {
        var original = _description;
        var target = Assert.Single(GpuIdentityService.GetDevices());
        var applied = await GpuIdentityService.ApplyAsync(target);
        Assert.True(applied.Ok); Assert.True(applied.Changed);
        Assert.Equal(GpuIdentityService.TargetDescription, _description);
        var repeat = await GpuIdentityService.ApplyAsync(Assert.Single(GpuIdentityService.GetDevices()));
        Assert.True(repeat.Ok); Assert.False(repeat.Changed); Assert.Equal(1, _writes);
        var result = await GpuIdentityService.RestoreAsync();
        Assert.Empty(result.Failures); Assert.Single(result.Restored);
        Assert.Equal(original, _description);
    }

    [Fact]
    public async Task Driver_change_and_foreign_name_are_not_overwritten()
    {
        var target = Assert.Single(GpuIdentityService.GetDevices());
        _driverVersion = "2.0";
        Assert.False((await GpuIdentityService.ApplyAsync(target)).Ok);
        Assert.Equal(0, _writes);
        Assert.True((await GpuIdentityService.ApplyAsync(Assert.Single(GpuIdentityService.GetDevices()))).Ok);
        _description = "changed by another tool";
        var result = await GpuIdentityService.RestoreAsync();
        Assert.Single(result.Failures); Assert.Empty(result.Restored);
        Assert.Equal("changed by another tool", _description);
    }

    [Fact]
    public async Task Permission_denial_preserves_original_and_can_be_resolved()
    {
        _failWrite = true;
        var original = _description;
        var result = await GpuIdentityService.ApplyAsync(Assert.Single(GpuIdentityService.GetDevices()));
        Assert.False(result.Ok); Assert.Equal(original, _description);
        Assert.True(File.Exists(result.BackupFile));
        _failWrite = false;
        Assert.Empty((await GpuIdentityService.RestoreAsync()).Failures);
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Enum\PCI\VEN_8086&DEV_1234\1")]
    [InlineData(@"SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_1234\1\other")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    public void Restore_target_cannot_escape_the_supported_display_device_path(string path)
    {
        Assert.False(GpuIdentityService.IsDevicePath(path));
        Assert.Throws<InvalidOperationException>(() => BackupService.ValidateRecord(new BackupRecord
        { Id = GpuIdentityService.BackupId, Hive = "LocalMachine", Path = path, Name = "DeviceDesc", Existed = true,
            ValueKind = "String", DeviceFingerprint = new string('A', 64) }));
    }
}
