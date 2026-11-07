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

    // ---------- 只读推导：真实型号 / 当前上报 / 是否被改名 ----------

    [Fact]
    public void Identity_readout_keeps_the_single_line_when_the_name_was_not_changed()
    {
        var snapshot = GpuIdentityService.ReadIdentity();
        Assert.True(snapshot.Available);
        Assert.False(snapshot.IsDisguised);
        Assert.Equal("NVIDIA GeForce RTX 3070", snapshot.RealName);     // DriverDesc
        Assert.Equal("NVIDIA GeForce RTX 3070", snapshot.ReportedName); // DeviceDesc 最后一段
        Assert.Null(snapshot.Failure);
    }

    [Fact]
    public void Identity_readout_reports_both_names_after_the_description_was_changed()
    {
        _description = "@oem1.inf,%gpu%;NVIDIA GeForce GTX 1050 Ti";
        var snapshot = GpuIdentityService.ReadIdentity();
        Assert.True(snapshot.IsDisguised);
        Assert.Equal("NVIDIA GeForce RTX 3070", snapshot.RealName);
        Assert.Equal("NVIDIA GeForce GTX 1050 Ti", snapshot.ReportedName);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", "NVIDIA GeForce RTX 3070", false)]
    [InlineData("nvidia geforce rtx 3070", "NVIDIA GeForce RTX 3070 ", false)]
    [InlineData("NVIDIA GeForce RTX 3070", "NVIDIA GeForce GTX 1050 Ti", true)]
    [InlineData("NVIDIA GeForce RTX 3070", "", false)]
    [InlineData("", "NVIDIA GeForce GTX 1050 Ti", false)]
    public void Disguise_derivation_only_compares_the_two_names(string real, string reported, bool disguised)
        => Assert.Equal(disguised, GpuIdentityService.IsDisguisedName(real, reported));

    [Fact]
    public void Identity_readout_prefers_the_device_the_overview_page_names()
    {
        const string other = @"PCI\VEN_10DE&DEV_2F18&SUBSYS_000010DE&REV_A1\4&abcdef&0&0008";
        RegistryHelper.SnapshotOverride = (_, path, name) =>
        {
            // Enum 键带 DEV_xxxx，Class 键只有 0000/0001：两处都要能区分是哪台设备。
            var second = path.Contains("DEV_2F18", StringComparison.OrdinalIgnoreCase) || path.EndsWith(@"\0001", StringComparison.Ordinal);
            return name switch
            {
                "ClassGUID" => new(true, "{4d36e968-e325-11ce-bfc1-08002be10318}", RegistryValueKind.String),
                "Driver" => new(true, @"{4d36e968-e325-11ce-bfc1-08002be10318}\" + (second ? "0001" : "0000"), RegistryValueKind.String),
                "HardwareID" => new(true, new[] { second ? @"PCI\VEN_10DE&DEV_2F18" : @"PCI\VEN_10DE&DEV_2484" }, RegistryValueKind.MultiString),
                "DriverVersion" => new(true, _driverVersion, RegistryValueKind.String),
                "DriverDesc" => new(true, second ? "NVIDIA GeForce GTX 1050 Ti" : "NVIDIA GeForce RTX 3070", RegistryValueKind.String),
                "DeviceDesc" => new(true, second ? "@oem.inf;NVIDIA GeForce GTX 1050 Ti" : "@oem.inf;NVIDIA GeForce RTX 3070", RegistryValueKind.String),
                _ => new(false, null, RegistryValueKind.Unknown),
            };
        };
        GpuIdentityService.DeviceIdsOverride = () => new[] { other, Instance };
        var devices = GpuIdentityService.ScanDevices().Devices;
        Assert.Equal(2, devices.Count);
        // 概览页给的型号（带显存后缀）能对上就取那台，多显卡机器不会选错。
        Assert.Equal(Instance, GpuIdentityService.PickDevice(devices, "NVIDIA GeForce RTX 3070 · 8 GB").InstanceId);
        Assert.Equal(other, GpuIdentityService.PickDevice(devices, null).InstanceId);
    }

    [Fact]
    public void Unreadable_devices_are_reported_with_their_reason_instead_of_a_bare_no_device()
    {
        // 两台 NVIDIA 设备都没法完整确认身份：不能只说「没有可用设备」。
        RegistryHelper.SnapshotOverride = (_, _, name) => name switch
        {
            "ClassGUID" => new(true, "{4d36e968-e325-11ce-bfc1-08002be10318}", RegistryValueKind.String),
            _ => new(false, null, RegistryValueKind.Unknown),
        };
        GpuIdentityService.DeviceIdsOverride = () => new[]
        {
            Instance,
            @"PCI\VEN_10DE&DEV_2F18&SUBSYS_000010DE&REV_A1\4&abcdef&0&0008\extra",
        };
        var scan = GpuIdentityService.ScanDevices();
        Assert.Empty(scan.Devices);
        Assert.Equal(2, scan.Failures.Count);
        Assert.False(scan.EnumerationFailed);
        Assert.Contains("Str.GpuIdentityUnavailable", scan.Failures[0]);

        var snapshot = GpuIdentityService.ReadIdentity();
        Assert.False(snapshot.Available);
        Assert.Contains("Str.GpuIdentityUnavailable", snapshot.Failure);
    }

    [Fact]
    public void Non_nvidia_adapters_are_skipped_instead_of_being_reported_as_unreadable()
    {
        // 核显与虚拟显示器不是本功能的候选：不该让用户看到「有设备读不到」。
        GpuIdentityService.DeviceIdsOverride = () => new[]
        {
            @"PCI\VEN_8086&DEV_7D55&SUBSYS_00008086&REV_00\3&11583659&0&10",
            @"ROOT\UNKNOWN\0000",
            Instance,
        };
        var scan = GpuIdentityService.ScanDevices();
        Assert.Single(scan.Devices);
        Assert.Empty(scan.Failures);
        Assert.False(GpuIdentityService.ReadIdentity().IsDisguised);
    }

    [Fact]
    public void Enumeration_failure_and_empty_list_are_reported_honestly()
    {
        GpuIdentityService.DeviceIdsOverride = () => throw new InvalidOperationException("WMI 不可用");
        var scan = GpuIdentityService.ScanDevices();
        Assert.True(scan.EnumerationFailed);
        Assert.Equal("WMI 不可用", scan.EnumerationError);
        Assert.Contains("str.gpuidentityscanfailed", GpuIdentityService.ReadIdentity().Failure!.ToLowerInvariant());

        GpuIdentityService.DeviceIdsOverride = () => [];
        var empty = GpuIdentityService.ScanDevices();
        Assert.False(empty.EnumerationFailed);
        Assert.Empty(empty.Devices);
        Assert.Empty(empty.Failures);
        Assert.Contains("str.gpuidentitynonelisted", GpuIdentityService.ReadIdentity().Failure!.ToLowerInvariant());
    }
}
