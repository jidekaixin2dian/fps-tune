using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using FpsTune.Wpf.Services;
using Microsoft.Win32;


namespace FpsTune.Wpf.Core;

public static partial class BackupService
{
    public static string Capture(IEnumerable<string> ids, string? gamePath)
    {
        var records = new List<BackupRecord>();
        foreach (var id in ids.Distinct())
            records.AddRange(CreateBackupRecords(id, gamePath));

        return WriteBackupFile(records);
    }

    /// <summary>
    /// 写入开机自启前先保存 HKCU Run 的原值（包括原本不存在），再执行同一项写入。
    /// 该入口让设置页的系统写入与优化项共享 BackupService 还原语义。
    /// </summary>
    public static string SetAutostart(bool enabled, string? executablePath)
    {
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(executablePath)
                || !Path.IsPathFullyQualified(executablePath)
                || executablePath.Contains('\0'))
                throw new InvalidOperationException("无法定位当前程序路径");
        }

        var record = CreateRegistryBackup(
            AutostartBackupId,
            RegistryHive.CurrentUser,
            AutostartRunKeyPath,
            AutostartRunValueName,
            RegistryValueKind.String);
        var backupFile = WriteBackupFile(new[] { record });
        if (enabled)
            RegistryHelper.SetValue(RegistryHive.CurrentUser, AutostartRunKeyPath, AutostartRunValueName,
                '"' + executablePath!.Trim() + '"', RegistryValueKind.String);
        else
            RegistryHelper.DeleteValue(RegistryHive.CurrentUser, AutostartRunKeyPath, AutostartRunValueName);
        return backupFile;
    }

    private static string WriteBackupFile(IReadOnlyList<BackupRecord> records)
    {
        Directory.CreateDirectory(BackupDir);
        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var file = Path.Combine(BackupDir, $"{CSharpBackupPrefix}{ts}-{suffix}.json");
            try
            {
                // CreateNew 保证并发调用不会覆盖另一份备份。
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(json);
                return file;
            }
            catch (IOException) when (File.Exists(file))
            {
                // 极低概率的路径冲突，换新的短 GUID 重试。
            }
        }

        throw new IOException("无法创建不覆盖现有文件的备份。");
    }

    public static IReadOnlyList<string> ListBackups()
    {
        Directory.CreateDirectory(BackupDir);
        return Directory.GetFiles(BackupDir, "*.json")
            .Where(IsCSharpBackupFile)
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
    }

    // 新旧格式均只认 C# 自己的前缀；旧的通用前缀再用 JSON 数组守卫，避免误读 PowerShell 文档。
    internal static bool IsCSharpBackupFile(string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith(CSharpBackupPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!name.StartsWith(LegacyBackupPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    public sealed class RestoreAllResult
    {
        public List<(string File, string Id)> Restored { get; } = new();
        public List<string> Failures { get; } = new();
    }

    private static IReadOnlyList<BackupRecord> CreateBackupRecords(string id, string? gamePath)
    {
        switch (id)
        {
            case "power-ultimate":
                return new[] { new BackupRecord { Id = id, Kind = "power-plan", OldActiveGuid = NativeSystem.GetActivePowerSchemeGuid() } };
            case "power-tuning":
                return new[]
                {
                    new BackupRecord
                    {
                        Id = id,
                        Kind = "power-tuning",
                        OldUsbValue = GetPowerAcIndex(
                            "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
                        OldBoostValue = GetPowerAcIndex(
                            "54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7")
                    }
                };
            case "sysmain-off":
            case "wsearch-off":
            {
                var serviceName = id == "sysmain-off" ? "SysMain" : "WSearch";
                return new[]
                {
                    new BackupRecord
                    {
                        Id = id,
                        Kind = "service",
                        ServiceName = serviceName,
                        OldStartValue = NativeSystem.GetServiceStartValue(serviceName),
                        OldStartMode = NativeSystem.GetServiceStartMode(serviceName)
                    }
                };
            }
            case "hibernate-off":
                return new[] { new BackupRecord { Id = id, Kind = "hibernate", OldState = NativeSystem.IsHibernateEnabled() ? "on" : "off" } };
            case "dyntick-off":
                return new[]
                {
                    new BackupRecord
                    {
                        Id = id,
                        Kind = "bcdedit",
                        OldState = NativeSystem.GetDynamicTickState().ToString().ToLowerInvariant()
                    }
                };
            case "gpu-pstate-lock":
            {
                var gpuPath = NativeSystem.GetMainGpuDriverKeyPath();
                if (gpuPath is null)
                    return Array.Empty<BackupRecord>();
                return new[] { CreateRegistryBackup(id, RegistryHive.LocalMachine, gpuPath, "DisableDynamicPstate", RegistryValueKind.DWord) };
            }
            case "fso-off":
            {
                if (string.IsNullOrWhiteSpace(gamePath))
                    return Array.Empty<BackupRecord>();
                return new[] { CreateRegistryBackup(id, RegistryHive.CurrentUser,
                    @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", gamePath, RegistryValueKind.String) };
            }
            case "gpu-pref":
            {
                if (string.IsNullOrWhiteSpace(gamePath))
                    return Array.Empty<BackupRecord>();
                return new[] { CreateRegistryBackup(id, RegistryHive.CurrentUser,
                    @"Software\Microsoft\DirectX\UserGpuPreferences", gamePath, RegistryValueKind.String) };
            }
            case "game-priority":
            {
                if (string.IsNullOrWhiteSpace(gamePath))
                    return Array.Empty<BackupRecord>();
                var gameName = Path.GetFileName(gamePath);
                var perfPath = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{gameName}\PerfOptions";
                return new[] { CreateRegistryBackup(id, RegistryHive.LocalMachine, perfPath, "CpuPriorityClass", RegistryValueKind.DWord) };
            }
            case "game-mode":
            {
                // 主值 + AllowAutoGameMode 一起备份，还原时按原值恢复。
                var record = CreateRegistryBackup(id, RegistryHive.CurrentUser,
                    @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord);
                FillSecondary(record, RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode");
                return new[] { record };
            }
            case "dvr-off":
            {
                // 主值 + AllowGameDVR 策略一起备份，还原时按原值恢复（不再无条件删除策略）。
                var record = CreateRegistryBackup(id, RegistryHive.CurrentUser,
                    @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord);
                FillSecondary(record, RegistryHive.LocalMachine,
                    @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
                return new[] { record };
            }
            case "keyboard-latency":
                return new[] { CreateRegistryBackup(id, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", RegistryValueKind.DWord) };
            case "keyboard-repeat":
                return new[]
                {
                    CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardDelay", RegistryValueKind.String),
                    CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardSpeed", RegistryValueKind.String),
                };
            case "sticky-keys-off":
                return new[]
                {
                    CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", RegistryValueKind.String),
                    CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", RegistryValueKind.String),
                };
            case "menu-delay-off":
                return new[] { CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", RegistryValueKind.String) };
            case "usb-power-save-off":
                return new[] { CreateRegistryBackup(id, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", RegistryValueKind.DWord) };
            case "visual-fx-perf":
                return new[] { CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Visual Effects", "VisualFXSetting", RegistryValueKind.DWord) };
            case "delivery-opt-off":
                return new[] { CreateRegistryBackup(id, RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", RegistryValueKind.DWord) };
            case "bg-apps-off":
                return new[] { CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", RegistryValueKind.DWord) };
            case "telemetry-off":
                return new[] { CreateRegistryBackup(id, RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Data Collection", "AllowTelemetry", RegistryValueKind.DWord) };
            case "net-nagle-off":
            {
                var records = new List<BackupRecord>();
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var interfaces = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
                if (interfaces is null)
                    return records;
                foreach (var sub in interfaces.GetSubKeyNames())
                {
                    var path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + sub;
                    var record = CreateRegistryBackup(id, RegistryHive.LocalMachine, path, "TcpAckFrequency", RegistryValueKind.DWord);
                    FillSecondary(record, RegistryHive.LocalMachine, path, "TCPNoDelay");
                    records.Add(record);
                }
                return records;
            }
            case "mouse-accel-off":
                // 三个 REG_SZ 值逐条备份，还原按原值恢复（缺失则回退系统默认）
                return new[]
                {
                    CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", RegistryValueKind.String),
                    CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", RegistryValueKind.String),
                    CreateRegistryBackup(id, RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", RegistryValueKind.String),
                };
            case "mmcss-games":
            {
                // 四个值逐条备份，还原时逐条按原值恢复。
                const string basePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
                return new[]
                {
                    CreateRegistryBackup(id, RegistryHive.LocalMachine, basePath, "GPU Priority", RegistryValueKind.DWord),
                    CreateRegistryBackup(id, RegistryHive.LocalMachine, basePath, "Priority", RegistryValueKind.DWord),
                    CreateRegistryBackup(id, RegistryHive.LocalMachine, basePath, "Scheduling Category", RegistryValueKind.String),
                    CreateRegistryBackup(id, RegistryHive.LocalMachine, basePath, "SFIO Priority", RegistryValueKind.String),
                };
            }
            default:
            {
                var spec = GetRegistrySpec(id);
                if (spec is null)
                    return Array.Empty<BackupRecord>();
                var (hive, path, name, kind) = spec.Value;
                return new[] { CreateRegistryBackup(id, hive, path, name, kind) };
            }
        }
    }

    // 记录第二个关联注册表值的原值（存在与否 + 值）。
    private static void FillSecondary(BackupRecord record, RegistryHive hive, string path, string name)
    {
        var exists = RegistryHelper.ValueExists(hive, path, name);
        record.SecondaryHive = hive.ToString();
        record.SecondaryPath = path;
        record.SecondaryName = name;
        record.SecondaryExisted = exists;
        record.SecondaryValue = exists ? RegistryHelper.ReadValue(hive, path, name) : null;
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

}
