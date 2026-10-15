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
            Assert.Contains(BackupService.EnumeratePendingRecords(), pending =>
                pending.Record.OldValue?.ToString() == _description || pending.Record.PostValue?.ToString() == _description);
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
    public async Task Selected_models_can_change_twice_and_restore_the_original_description()
    {
        var original = _description;
        foreach (var preset in GpuIdentityService.Presets.Take(2))
        {
            var result = await GpuIdentityService.ApplyAsync(Assert.Single(GpuIdentityService.GetDevices()), preset.Description);
            Assert.True(result.Ok, result.Message); Assert.Equal(preset.Description, _description);
        }
        var restored = await GpuIdentityService.RestoreAsync();
        Assert.Empty(restored.Failures); Assert.Equal(original, _description);
    }

    [Fact]
    public void Device_labels_do_not_expose_internal_record_fields_and_unknown_presets_never_write()
    {
        var target = Assert.Single(GpuIdentityService.GetDevices());
        Assert.Equal(target.DriverName, target.ToString());
        Assert.Equal("NVIDIA GeForce RTX 3070", target.CurrentName);
        Assert.Contains(GpuIdentityService.Presets, p => p.Name == "GTX 750 Ti");
        Assert.Throws<ArgumentException>(() => { _ = GpuIdentityService.ApplyAsync(target, "unexpected arbitrary value"); });
        Assert.Equal(0, _writes);
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
