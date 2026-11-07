using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public sealed record GpuIdentityTarget(string InstanceId, string DriverName, string Description, string Fingerprint)
{
    public string DisplayName => DriverName;
    public string CurrentName => Description[(Description.LastIndexOf(';') + 1)..];
    public override string ToString() => DisplayName;
}
public sealed record GpuNamePreset(string Name, string Family)
{
    public string Description => "NVIDIA GeForce " + Name;
    public string Series => Str.T("Str.GpuIdentitySeries", Family);
    public bool IsRecommended => Name is "GTX 750 Ti" or "GTX 1050 Ti";
}
public sealed record GpuIdentityResult(bool Ok, bool Changed, string Message, string? BackupFile);

/// <summary>
/// 一次设备枚举的结果：能读到的设备 + 没读到的原因。
/// 以前单台设备抛异常被整体吞掉，界面上只剩一句「没有可用设备」，
/// 用户分不清是没装 N 卡、WMI 坏了，还是设备真的不完整。
/// </summary>
public sealed record GpuDeviceScan(IReadOnlyList<GpuIdentityTarget> Devices, IReadOnlyList<string> Failures,
    string? EnumerationError)
{
    /// <summary>设备枚举本身失败（例如查询在线设备列表时出错）。</summary>
    public bool EnumerationFailed => EnumerationError is not null;
}

/// <summary>
/// 伪装状态的只读推导：真实型号 = 驱动写入的 <c>DriverDesc</c>，
/// 当前上报 = 设备键 <c>DeviceDesc</c> 的最后一段。两者不同即视为已被改名。
/// </summary>
public sealed record GpuIdentitySnapshot(string? RealName, string? ReportedName, bool IsDisguised, string? Failure)
{
    public bool Available => RealName is not null && ReportedName is not null;
}

/// <summary>社区兼容实验：只修改显示设备的 DeviceDesc，不更改硬件 ID 或驱动文件。</summary>
public static class GpuIdentityService
{
    public const string TargetDescription = "NVIDIA GeForce GTX 1050 Ti";
    public static IReadOnlyList<GpuNamePreset> Presets { get; } = Array.AsReadOnly(new[]
    {
        new GpuNamePreset("GTX 750 Ti", "GTX 700"), new("GTX 1050 Ti", "GTX 10"),
        new("GTX 950", "GTX 900"), new("GTX 960", "GTX 900"),
        new("GTX 1060 6GB", "GTX 10"), new("GTX 1650", "GTX 16"),
        new("GTX 1660 SUPER", "GTX 16"), new("RTX 2060", "RTX 20")
    });
    internal const string BackupId = "gpu-description";
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    internal static Func<IEnumerable<string>>? DeviceIdsOverride { get; set; }
    internal static Func<bool>? AdminCheckOverride { get; set; }

    public static IReadOnlyList<GpuIdentityTarget> GetDevices() => ScanDevices().Devices;

    /// <summary>枚举显示设备，同时收集读不到的原因（不静默丢弃）。</summary>
    public static GpuDeviceScan ScanDevices()
    {
        List<string> ids;
        try
        {
            ids = (DeviceIdsOverride?.Invoke() ?? ReadDeviceIds())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return new(Array.Empty<GpuIdentityTarget>(), Array.Empty<string>(), reason);
        }

        var devices = new List<GpuIdentityTarget>();
        var failures = new List<string>();
        foreach (var id in ids)
        {
            if (!IsDevicePath(@"SYSTEM\CurrentControlSet\Enum\" + id))
            {
                // 核显/虚拟显示器本来就不是本功能的候选，不该算成「读不到」；
                // 只有看着像 NVIDIA 设备、路径却不完整时才记下原因。
                if (id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
                    failures.Add(Str.T("Str.GpuIdentityUnavailable"));
                continue;
            }
            try { devices.Add(ReadTarget(id)); }
            catch (Exception ex)
            {
                // 记下这台设备为什么没被列出：可能就是用户找的那台。
                // 同一张 N 卡上的音频功能（媒体类设备）不是候选，不报。
                if (!IsDisplayClassDevice(id)) continue;
                failures.Add(string.IsNullOrWhiteSpace(ex.Message) ? Str.T("Str.GpuIdentityUnavailable") : ex.Message);
            }
        }
        return new(devices, failures, null);
    }

    /// <summary>这个设备项的 ClassGUID 是不是显示类；读不到 ClassGUID 时按「可能是」处理。</summary>
    private static bool IsDisplayClassDevice(string instanceId)
    {
        try
        {
            var classGuid = RegistryHelper.ReadValue(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId, "ClassGUID") as string;
            return classGuid is null || string.Equals(classGuid, DisplayClass, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 只读推导当前伪装状态。<paramref name="preferredName"/> 传概览页显示的型号时，
    /// 优先取名字对得上的那台设备，避免多显卡机器选错。
    /// </summary>
    public static GpuIdentitySnapshot ReadIdentity(string? preferredName = null)
    {
        var scan = ScanDevices();
        if (scan.EnumerationFailed)
            return new(null, null, false, Str.T("Str.GpuIdentityScanFailed", scan.EnumerationError));
        if (scan.Devices.Count == 0)
            return new(null, null, false, scan.Failures.FirstOrDefault() ?? Str.T("Str.GpuIdentityNoneListed"));
        var device = PickDevice(scan.Devices, preferredName);
        return new(device.DriverName, device.CurrentName,
            IsDisguisedName(device.DriverName, device.CurrentName), null);
    }

    internal static GpuIdentityTarget PickDevice(IReadOnlyList<GpuIdentityTarget> devices, string? preferredName)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
            foreach (var device in devices)
                if (!string.IsNullOrWhiteSpace(device.DriverName)
                    && preferredName.Contains(device.DriverName, StringComparison.OrdinalIgnoreCase))
                    return device;
        return devices[0];
    }

    /// <summary>两个名字是否算「被改过」：都不为空且不完全相同。</summary>
    public static bool IsDisguisedName(string? realName, string? reportedName)
        => !string.IsNullOrWhiteSpace(realName) && !string.IsNullOrWhiteSpace(reportedName)
           && !string.Equals(realName.Trim(), reportedName.Trim(), StringComparison.OrdinalIgnoreCase);

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

    public static Task<GpuIdentityResult> ApplyAsync(GpuIdentityTarget target, string description = TargetDescription)
    {
        if (!Presets.Any(p => p.Description == description)) throw new ArgumentException("Unknown GPU name preset.", nameof(description));
        return Task.Run(() => Apply(target, description));
    }

    private static GpuIdentityResult Apply(GpuIdentityTarget target, string description)
    {
        if (!(AdminCheckOverride?.Invoke() ?? AdminHelper.IsAdministrator()))
            return new(false, false, Str.T("Str.NeedsAdmin"), null);
        using var gate = SystemMutationGate.Acquire();
        var current = ReadTarget(target.InstanceId);
        if (current.Fingerprint != target.Fingerprint)
            return new(false, false, Str.T("Str.GpuIdentityChanged"), null);
        if (current.Description == description)
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
            RegistryHelper.SetValue(RegistryHive.LocalMachine, record.Path, record.Name, description, RegistryValueKind.String);
            if (RegistryHelper.ReadValue(RegistryHive.LocalMachine, record.Path, record.Name) as string != description)
                throw new IOException(Str.T("Str.GpuIdentityWriteFailed"));
            BackupService.CompleteCapture(file, new(BackupId, BackupId, true, true, false, ""));
            return new(true, true, Str.T("Str.GpuIdentityApplied", description), file);
        }
        catch (Exception ex)
        {
            try { BackupService.CompleteCapture(file, new(BackupId, BackupId, false, false, false, ex.Message, true)); } catch { }
            return new(false, false, Str.T("Str.GpuIdentityWriteFailed") + " " + ex.Message, file);
        }
    }

    public static Task<BackupService.RestoreAllResult> RestoreAsync() => Task.Run(() => BackupService.RestoreAll(new[] { BackupId }));
}
