using System.Management;

namespace FpsTune.Wpf.Services;

/// <summary>一块物理盘的只读健康信息。Detail 是已取得的 SMART/可靠性字段，不用推断值填充。</summary>
public sealed record DiskHealthRow(string Name, long SizeBytes, string Bus, string Health, string Detail, bool Detailed);

public sealed record DiskHealthSnapshot(IReadOnlyList<DiskHealthRow> Disks, string Boundary, string? Error = null);

public interface IDiskHealthApi
{
    DiskHealthSnapshot Read();
}

/// <summary>
/// 磁盘健康只读扫描：Win32_DiskDrive 概况始终可尝试；
/// 温度、磨损、通电时间来自 MSFT_StorageReliabilityCounter，缺权限/不支持时如实降级。
/// 不执行清理、碎片整理或任何写操作。
/// </summary>
public static class DiskHealthService
{
    internal static Func<IDiskHealthApi>? ApiOverride { get; set; }

    public static DiskHealthSnapshot Read()
    {
        try { return (ApiOverride?.Invoke() ?? new WmiDiskHealthApi()).Read(); }
        catch (Exception ex)
        {
            return new([], Str.T("Str.DiskHealthUnavailable"), PrivacyScrub.Sanitize(ex.Message));
        }
    }

    internal static string FormatSize(long bytes)
        => bytes <= 0 ? "—" : $"{bytes / (1024d * 1024 * 1024 * 1024):0.#} TB";
}

internal sealed class WmiDiskHealthApi : IDiskHealthApi
{
    public DiskHealthSnapshot Read()
    {
        var details = ReadReliability(out var detailed);
        var rows = new List<DiskHealthRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model,Size,InterfaceType,MediaType,Status,DeviceID,Index FROM Win32_DiskDrive");
            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    var device = disk["DeviceID"]?.ToString() ?? "";
                    var index = device.StartsWith(@"\\.\PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase)
                        ? device[@"\\.\PHYSICALDRIVE".Length..] : disk["Index"]?.ToString() ?? "";
                    var name = (disk["Model"]?.ToString() ?? Str.T("Str.DiskHealthUnknownDisk")).Trim();
                    var bus = (disk["InterfaceType"]?.ToString() ?? "").Trim();
                    var media = (disk["MediaType"]?.ToString() ?? "").Trim();
                    if (media.Length > 0 && !bus.Contains(media, StringComparison.OrdinalIgnoreCase))
                        bus = bus.Length == 0 ? media : bus + " · " + media;
                    var status = (disk["Status"]?.ToString() ?? "").Trim();
                    var health = status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? Str.T("Str.DiskHealthOk")
                        : string.IsNullOrWhiteSpace(status) ? Str.T("Str.DiskHealthUnknown") : status;
                    details.TryGetValue(index, out var detail);
                    var size = long.TryParse(disk["Size"]?.ToString(), out var parsed) ? parsed : 0;
                    rows.Add(new(name, size, bus, health,
                        detail ?? Str.T("Str.DiskHealthBasicOnly"), detail is not null));
                }
            }
            var boundary = rows.Count == 0 ? Str.T("Str.DiskHealthNoDisk")
                : rows.All(r => r.Detailed) ? Str.T("Str.DiskHealthDetailedReady")
                : Str.T("Str.DiskHealthDetailBoundary");
            return new(rows.OrderBy(r => r.Name, StringComparer.CurrentCulture).ToList(), boundary);
        }
        catch (Exception ex)
        {
            return new([], Str.T("Str.DiskHealthUnavailable"), PrivacyScrub.Sanitize(ex.Message));
        }
    }

    private static Dictionary<string, string> ReadReliability(out bool available)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        available = false;
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId,Temperature,TemperatureMax,Wear,PowerOnHours,ReadErrorsTotal,WriteErrorsTotal FROM MSFT_StorageReliabilityCounter"));
            foreach (ManagementObject counter in searcher.Get())
            {
                using (counter)
                {
                    var id = counter["DeviceId"]?.ToString() ?? "";
                    var parts = new List<string>();
                    void Add(string label, string key, string suffix = "")
                    {
                        var value = counter[key]?.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) parts.Add(label + " " + value + suffix);
                    }
                    Add(Str.T("Str.DiskHealthTemp"), "Temperature", "°C");
                    Add(Str.T("Str.DiskHealthTempMax"), "TemperatureMax", "°C");
                    Add(Str.T("Str.DiskHealthWear"), "Wear", "%");
                    Add(Str.T("Str.DiskHealthPowerOn"), "PowerOnHours", " h");
                    Add(Str.T("Str.DiskHealthReadErrors"), "ReadErrorsTotal");
                    Add(Str.T("Str.DiskHealthWriteErrors"), "WriteErrorsTotal");
                    if (parts.Count > 0) { result[id] = string.Join(" · ", parts); available = true; }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ManagementException or System.Runtime.InteropServices.COMException)
        {
            // Windows 10/11 通常要管理员才能读取可靠性计数器；能力边界由调用方如实展示。
            available = false;
        }
        return result;
    }
}
