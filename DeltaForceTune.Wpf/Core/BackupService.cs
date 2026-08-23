using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DeltaForceTune.Wpf.Core;

public sealed record BackupRecord(
    string Id,
    string Hive,
    string Path,
    string Name,
    object? OldValue,
    string ValueKind,
    bool Existed);

public static class BackupService
{
    private static string BackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeltaForceTune", "backup");

    public static string Capture(IEnumerable<string> ids, string? gamePath)
    {
        var records = new List<BackupRecord>();
        foreach (var id in ids.Distinct())
        {
            var spec = GetRegistrySpec(id);
            if (spec is null) continue;
            var (hive, path, name, kind) = spec.Value;
            var exists = RegistryHelper.ValueExists(hive, path, name);
            var old = exists ? RegistryHelper.ReadValue(hive, path, name) : null;
            records.Add(new BackupRecord(id, hive.ToString(), path, name, old, kind.ToString(), exists));
        }

        Directory.CreateDirectory(BackupDir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(BackupDir, $"backup-{ts}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    public static string? RestoreLatest()
    {
        Directory.CreateDirectory(BackupDir);
        var file = Directory.GetFiles(BackupDir, "backup-*.json")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (file is null) return null;

        var records = JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(file));
        if (records is null) return null;

        foreach (var r in records)
        {
            var hive = Enum.Parse<RegistryHive>(r.Hive);
            var kind = Enum.Parse<RegistryValueKind>(r.ValueKind);
            if (r.Existed && r.OldValue is not null)
                RegistryHelper.SetValue(hive, r.Path, r.Name, ConvertValue(r.OldValue, kind), kind);
            else
                RegistryHelper.DeleteValue(hive, r.Path, r.Name);
        }

        return file;
    }

    private static object ConvertValue(object value, RegistryValueKind kind)
    {
        if (kind == RegistryValueKind.DWord)
            return Convert.ToInt32(value);
        if (kind == RegistryValueKind.QWord)
            return Convert.ToInt64(value);
        return value.ToString() ?? "";
    }

    private static (RegistryHive, string, string, RegistryValueKind)? GetRegistrySpec(string id)
    {
        return id switch
        {
            "hags" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueKind.DWord),
            "game-mode" => (RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord),
            "dvr-off" => (RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord),
            "prio-separation" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", RegistryValueKind.DWord),
            "wer-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", RegistryValueKind.DWord),
            "transparency-off" => (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", RegistryValueKind.DWord),
            "mpo-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", RegistryValueKind.DWord),
            "net-throttling-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", RegistryValueKind.DWord),
            "sys-responsiveness" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", RegistryValueKind.DWord),
            "mmcss-games" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", RegistryValueKind.DWord),
            "paging-exec" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", RegistryValueKind.DWord),
            "mem-compress-off" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCompression", RegistryValueKind.DWord),
            _ => null
        };
    }
}
