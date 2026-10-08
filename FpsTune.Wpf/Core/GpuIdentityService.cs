using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public sealed record GpuIdentityTarget(string InstanceId, string DriverName, string Description, string Fingerprint);
public sealed record GpuIdentityResult(bool Ok, bool Changed, string Message, string? BackupFile);

/// <summary>社区兼容实验：只修改显示设备的 DeviceDesc，不更改硬件 ID 或驱动文件。</summary>
public static class GpuIdentityService
{
    public const string TargetDescription = "NVIDIA GeForce GTX 1050 Ti";
    internal const string BackupId = "gpu-description";
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    internal static Func<IEnumerable<string>>? DeviceIdsOverride { get; set; }
    internal static Func<bool>? AdminCheckOverride { get; set; }

    public static IReadOnlyList<GpuIdentityTarget> GetDevices()
    {
        var devices = new List<GpuIdentityTarget>();
        foreach (var id in DeviceIdsOverride?.Invoke() ?? ReadDeviceIds())
        {
            if (!IsDevicePath(@"SYSTEM\CurrentControlSet\Enum\" + id)) continue;
            try { devices.Add(ReadTarget(id)); }
            catch { /* 不显示未能完整确认身份的设备。 */ }
        }
        return devices;
    }

    private static IEnumerable<string> ReadDeviceIds()
    {
        var ids = new List<string>();
        using var query = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_VideoController");
        using var results = query.Get();
        foreach (ManagementObject item in results)
        {
            using (item) { if (item["PNPDeviceID"] is string id) ids.Add(id); }
        }
        return ids;
    }

    internal static bool IsDevicePath(string path) => path.Length <= 512 && Regex.IsMatch(path,
        @"^SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_10DE&DEV_[0-9A-F]{4}(?:&[A-Z0-9_]+)*\\[A-Z0-9&_-]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static GpuIdentityTarget ReadTarget(string instanceId)
    {
        var path = @"SYSTEM\CurrentControlSet\Enum\" + instanceId;
        if (!IsDevicePath(path)) throw new InvalidOperationException(Str.T("Str.GpuIdentityUnavailable"));
        object? Read(string name) => RegistryHelper.ReadValue(RegistryHive.LocalMachine, path, name);
        if (!string.Equals(Read("ClassGUID") as string, DisplayClass, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Str.T("Str.GpuIdentityUnavailable"));
        var driver = Read("Driver") as string;
        if (driver is null || !Regex.IsMatch(driver,
            @"^\{4d36e968-e325-11ce-bfc1-08002be10318\}\\\d{4}$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException(Str.T("Str.GpuIdentityUnavailable"));
        var hardware = Read("HardwareID") as string[];
        if (hardware is null || hardware.Length == 0 || hardware.Length > 32 || hardware.Any(h => h.Length > 512)
            || !hardware.Any(h => h.StartsWith(@"PCI\VEN_10DE&DEV_", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(Str.T("Str.GpuIdentityUnavailable"));
        var description = RegistryHelper.ReadSnapshot(RegistryHive.LocalMachine, path, "DeviceDesc");
        if (!description.Existed || description.Kind != RegistryValueKind.String || description.Value is not string text || text.Length > 2048)
            throw new InvalidOperationException(Str.T("Str.GpuIdentityUnavailable"));
        var driverPath = @"SYSTEM\CurrentControlSet\Control\Class\" + driver;
        var name = RegistryHelper.ReadValue(RegistryHive.LocalMachine, driverPath, "DriverDesc") as string ?? "NVIDIA";
        var version = RegistryHelper.ReadValue(RegistryHive.LocalMachine, driverPath, "DriverVersion") as string ?? "";
        var identity = JsonSerializer.Serialize(new { Instance = instanceId.ToUpperInvariant(), Driver = driver.ToUpperInvariant(),
            Version = version, Hardware = hardware.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).Select(h => h.ToUpperInvariant()) });
        return new(instanceId, name, text, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    internal static void VerifyIdentity(BackupRecord record)
    {
        var instance = record.Path[@"SYSTEM\CurrentControlSet\Enum\".Length..];
        if (ReadTarget(instance).Fingerprint != record.DeviceFingerprint)
            throw new BackupCompatibilityException(Str.T("Str.GpuIdentityChanged"));
    }

    public static Task<GpuIdentityResult> ApplyAsync(GpuIdentityTarget target) => Task.Run(() => Apply(target));

    private static GpuIdentityResult Apply(GpuIdentityTarget target)
    {
        if (!(AdminCheckOverride?.Invoke() ?? AdminHelper.IsAdministrator()))
            return new(false, false, Str.T("Str.NeedsAdmin"), null);
        using var gate = SystemMutationGate.Acquire();
        var current = ReadTarget(target.InstanceId);
        if (current.Fingerprint != target.Fingerprint)
            return new(false, false, Str.T("Str.GpuIdentityChanged"), null);
        if (current.Description == TargetDescription)
            return new(true, false, Str.T("Str.GpuIdentityAlready"), null);
        BackupService.EnsureNoPendingWrites(new[] { BackupId });
        var record = new BackupRecord
        {
            Id = BackupId, Kind = "registry", Hive = RegistryHive.LocalMachine.ToString(),
            Path = @"SYSTEM\CurrentControlSet\Enum\" + current.InstanceId, Name = "DeviceDesc",
            Existed = true, OldValue = current.Description, ValueKind = RegistryValueKind.String.ToString(),
            DeviceFingerprint = current.Fingerprint, MutationState = "pending"
        };
        var file = BackupService.CaptureGpuDescription(record);
        try
        {
            VerifyIdentity(record);
            // 不修改设备权限；系统拒绝写入时直接返回，原始备份仍可审查。
            RegistryHelper.SetValue(RegistryHive.LocalMachine, record.Path, record.Name, TargetDescription, RegistryValueKind.String);
            if (RegistryHelper.ReadValue(RegistryHive.LocalMachine, record.Path, record.Name) as string != TargetDescription)
                throw new IOException(Str.T("Str.GpuIdentityWriteFailed"));
            BackupService.CompleteCapture(file, new(BackupId, BackupId, true, true, false, ""));
            return new(true, true, Str.T("Str.GpuIdentityApplied"), file);
        }
        catch (Exception ex)
        {
            try { BackupService.CompleteCapture(file, new(BackupId, BackupId, false, false, false, ex.Message, true)); } catch { }
            return new(false, false, Str.T("Str.GpuIdentityWriteFailed") + " " + ex.Message, file);
        }
    }

    public static Task<BackupService.RestoreAllResult> RestoreAsync() => Task.Run(() => BackupService.RestoreAll(new[] { BackupId }));
}
