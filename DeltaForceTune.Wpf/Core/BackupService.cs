using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaForceTune.Wpf.Core;

public sealed class BackupRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "registry";
    public string Hive { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public object? OldValue { get; set; }
    public string ValueKind { get; set; } = "DWord";
    public bool Existed { get; set; }
    public string? ServiceName { get; set; }
    public int? OldStartValue { get; set; }
    public string? OldStartMode { get; set; }
    public string? OldActiveGuid { get; set; }
    public string? OldState { get; set; }
}

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
            var record = CreateBackupRecord(id, gamePath);
            if (record is not null)
                records.Add(record);
        }

        Directory.CreateDirectory(BackupDir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(BackupDir, $"backup-{ts}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    public static IReadOnlyList<string> ListBackups()
    {
        Directory.CreateDirectory(BackupDir);
        return Directory.GetFiles(BackupDir, "backup-*.json")
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
    }

    public static string? RestoreLatest()
    {
        Directory.CreateDirectory(BackupDir);
        var file = Directory.GetFiles(BackupDir, "backup-*.json")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (file is null)
            return null;

        var records = JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(file));
        if (records is null)
            return null;

        RestoreRecords(records);
        return file;
    }

    public static void RestoreRecords(IEnumerable<BackupRecord> records)
    {
        foreach (var r in records)
        {
            try
            {
                RestoreOne(r);
            }
            catch
            {
                // 单条还原失败不阻断整批；调用方可在汇总时说明。
            }
        }
    }

    private static BackupRecord? CreateBackupRecord(string id, string? gamePath)
    {
        switch (id)
        {
            case "power-ultimate":
                return new BackupRecord
                {
                    Id = id,
                    Kind = "power-plan",
                    OldActiveGuid = NativeSystem.GetActivePowerSchemeGuid()
                };
            case "power-tuning":
                return new BackupRecord { Id = id, Kind = "power-tuning" };
            case "sysmain-off":
            case "wsearch-off":
                var serviceName = id == "sysmain-off" ? "SysMain" : "WSearch";
                return new BackupRecord
                {
                    Id = id,
                    Kind = "service",
                    ServiceName = serviceName,
                    OldStartValue = NativeSystem.GetServiceStartValue(serviceName),
                    OldStartMode = NativeSystem.GetServiceStartMode(serviceName)
                };
            case "hibernate-off":
                return new BackupRecord
                {
                    Id = id,
                    Kind = "hibernate",
                    OldState = NativeSystem.IsHibernateEnabled() ? "on" : "off"
                };
            case "dyntick-off":
                return new BackupRecord
                {
                    Id = id,
                    Kind = "bcdedit",
                    OldState = NativeSystem.IsDynamicTickEnabled() ? "on" : "off"
                };
            case "gpu-pstate-lock":
                var gpuPath = NativeSystem.GetMainGpuDriverKeyPath();
                if (gpuPath is null)
                    return null;
                return CreateRegistryBackup(id, RegistryHive.LocalMachine, gpuPath, "DisableDynamicPstate", RegistryValueKind.DWord);
            case "fso-off":
                if (string.IsNullOrWhiteSpace(gamePath))
                    return null;
                return CreateRegistryBackup(id, RegistryHive.CurrentUser,
                    @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", gamePath, RegistryValueKind.String);
            case "gpu-pref":
                if (string.IsNullOrWhiteSpace(gamePath))
                    return null;
                return CreateRegistryBackup(id, RegistryHive.CurrentUser,
                    @"Software\Microsoft\DirectX\UserGpuPreferences", gamePath, RegistryValueKind.String);
            case "game-priority":
                if (string.IsNullOrWhiteSpace(gamePath))
                    return null;
                var gameName = Path.GetFileName(gamePath);
                var perfPath = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{gameName}\PerfOptions";
                return CreateRegistryBackup(id, RegistryHive.LocalMachine, perfPath, "CpuPriorityClass", RegistryValueKind.DWord);
            default:
                var spec = GetRegistrySpec(id);
                if (spec is null)
                    return null;
                var (hive, path, name, kind) = spec.Value;
                return CreateRegistryBackup(id, hive, path, name, kind);
        }
    }

    private static BackupRecord CreateRegistryBackup(
        string id, RegistryHive hive, string path, string name, RegistryValueKind kind)
    {
        var exists = RegistryHelper.ValueExists(hive, path, name);
        var old = exists ? RegistryHelper.ReadValue(hive, path, name) : null;
        return new BackupRecord
        {
            Id = id,
            Kind = "registry",
            Hive = hive.ToString(),
            Path = path,
            Name = name,
            OldValue = old,
            ValueKind = kind.ToString(),
            Existed = exists
        };
    }

    private static void RestoreOne(BackupRecord r)
    {
        switch (r.Kind)
        {
            case "registry":
                RestoreRegistry(r);
                break;
            case "service":
                RestoreService(r);
                break;
            case "power-plan":
                RestorePowerPlan(r);
                break;
            case "power-tuning":
                RestorePowerTuning();
                break;
            case "hibernate":
                RestoreHibernate(r);
                break;
            case "bcdedit":
                RestoreBcdedit(r);
                break;
        }
    }

    private static void RestoreRegistry(BackupRecord r)
    {
        var hive = Enum.Parse<RegistryHive>(r.Hive);
        var kind = Enum.Parse<RegistryValueKind>(r.ValueKind);
        if (r.Existed && r.OldValue is not null)
            RegistryHelper.SetValue(hive, r.Path, r.Name, ConvertValue(r.OldValue, kind), kind);
        else
            RegistryHelper.DeleteValue(hive, r.Path, r.Name);

        // 与原始 PowerShell 引擎保持一致：游戏模式还原时固定保留 AllowAutoGameMode=1。
        if (r.Id == "game-mode")
            RegistryHelper.SetValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord);

        // DVR 还原时删除策略键值（原脚本不备份此策略，统一删除）。
        if (r.Id == "dvr-off")
            RegistryHelper.DeleteValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
    }

    private static void RestoreService(BackupRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.ServiceName))
            return;

        var mode = r.OldStartMode;
        if (string.IsNullOrWhiteSpace(mode))
        {
            mode = r.OldStartValue switch
            {
                0 => "boot",
                1 => "system",
                2 => "auto",
                3 => "demand",
                4 => "disabled",
                _ => "demand"
            };
        }

        // sc.exe 的参数中间必须保留空格，例如 "start= demand"。
        NativeSystem.Run("sc.exe", "config", r.ServiceName, "start=", mode);
    }

    private static void RestorePowerPlan(BackupRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.OldActiveGuid))
            return;

        NativeSystem.Run("powercfg.exe", "-setactive", r.OldActiveGuid);
    }

    private static void RestorePowerTuning()
    {
        NativeSystem.Run("powercfg.exe", "-setacvalueindex", "SCHEME_CURRENT",
            "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", "1");
        NativeSystem.Run("powercfg.exe", "-setacvalueindex", "SCHEME_CURRENT",
            "be337238-0d82-4146-a960-4f3749d470c7", "45bcc044-d885-43e2-8605-ee0ec6e96b59", "0");
        NativeSystem.Run("powercfg.exe", "-setacvalueindex", "SCHEME_CURRENT",
            "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d", "4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3", "1");
        NativeSystem.Run("powercfg.exe", "-setactive", "SCHEME_CURRENT");
    }

    private static void RestoreHibernate(BackupRecord r)
    {
        if (r.OldState == "on")
            NativeSystem.Run("powercfg.exe", "/h", "on");
    }

    private static void RestoreBcdedit(BackupRecord r)
    {
        if (r.OldState == "on")
            NativeSystem.Run("bcdedit.exe", "/set", "{current}", "disabledynamictick", "no");
    }

    private static object ConvertValue(object value, RegistryValueKind kind)
    {
        if (value is JsonElement element)
        {
            if (kind == RegistryValueKind.DWord)
            {
                if (element.TryGetInt32(out var intValue))
                    return intValue;
                if (element.TryGetInt64(out var longValue))
                    return (int)longValue;
                if (element.TryGetUInt64(out var ulongValue))
                    return unchecked((int)ulongValue);
            }
            if (kind == RegistryValueKind.QWord)
            {
                if (element.TryGetInt64(out var longValue))
                    return longValue;
                if (element.TryGetUInt64(out var ulongValue))
                    return unchecked((long)ulongValue);
            }
            return element.GetString() ?? "";
        }

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
