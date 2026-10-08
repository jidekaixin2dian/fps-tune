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

    internal static string CaptureGpuDescription(BackupRecord record)
    {
        ValidateRecord(record);
        GpuIdentityService.VerifyIdentity(record);
        return WriteBackupFile(new[] { record });
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
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var file = Path.Combine(BackupDir, $"{V2BackupPrefix}{ts}-{suffix}.json");
            var json = SerializeRecords(file, records);
            try
            {
                // CreateNew 保证并发调用不会覆盖另一份备份。
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
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

    /// <summary>0.2.2 M4：枚举全部备份文件里的未还原记录（供改动总览）。只读。</summary>
    internal static IReadOnlyList<(string FileName, DateTime LastWrite, BackupRecord Record)> EnumeratePendingRecords()
    {
        var result = new List<(string, DateTime, BackupRecord)>();
        foreach (var file in ListBackups())
        {
            try
            {
                var lastWrite = File.GetLastWriteTime(file);
                var records = ReadRecords(file);
                if (records is null)
                    continue;
                foreach (var r in records.Where(r => !r.Restored))
                    result.Add((Path.GetFileName(file), lastWrite, r));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception)
            {
                // 单个坏文件不拖垮总览
            }
        }
        return result;
    }

    /// <summary>备份文件的状态行：待还原 / 已消费（.restored 审计）/ 无法读取。</summary>
    public sealed record BackupFileStatus(
        string FileName,
        DateTime LastWrite,
        int PendingCount,
        int RestoredCount,
        bool Valid);

    /// <summary>
    /// 枚举备份目录里的全部 C# 备份，**含已消费的 .json.restored 审计文件**，
    /// 供备份页展示"哪些还能还原、哪些已经用掉"（P2-11）。只读，不改任何文件。
    /// </summary>
    public static IReadOnlyList<BackupFileStatus> ListBackupStatuses()
    {
        Directory.CreateDirectory(BackupDir);
        var result = new List<BackupFileStatus>();
        foreach (var file in Directory.EnumerateFiles(BackupDir, "*"))
        {
            var name = Path.GetFileName(file);
            var isJson = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var isConsumed = name.EndsWith(".json.restored", StringComparison.OrdinalIgnoreCase);
            if (!isJson && !isConsumed)
                continue;
            // 与 ListBackups 同一识别口径：C# 前缀直接认，legacy 前缀用 JSON 数组守卫
            if (!IsCSharpBackupFile(file))
                continue;

            DateTime lastWrite;
            int pending = 0, restored = 0;
            var valid = true;
            try
            {
                lastWrite = File.GetLastWriteTime(file);
                var records = ReadRecords(file);
                if (records is null)
                {
                    valid = false;
                }
                else
                {
                    pending = records.Count(r => !r.Restored);
                    restored = records.Count - pending;
                }
            }
            catch (FileNotFoundException) { continue; }   // 读取间隙被还原流程重命名，直接跳过
            catch (DirectoryNotFoundException) { continue; }
            catch (Exception)
            {
                valid = false;
                lastWrite = File.GetLastWriteTime(file);
            }

            result.Add(new BackupFileStatus(name, lastWrite, pending, restored, valid));
        }

        return result.OrderByDescending(s => s.LastWrite).ToList();
    }

    /// <summary>
    /// 待还原备份中是否含有需要管理员权限的记录（电源/服务/HKLM 注册表）。
    /// 供 GUI 还原入口做提权预检，与「应用」路径一致。只读；单个文件解析失败时跳过，
    /// 不阻塞判断（该文件在还原时自有失败反馈）。
    /// </summary>
    public static bool RestoreNeedsAdmin()
    {
        foreach (var file in ListBackups())
        {
            List<BackupRecord>? records;
            try
            {
                records = ReadRecords(file);
            }
            catch
            {
                continue;
            }
            if (records is null)
                continue;
            foreach (var record in records)
            {
                if (record.Restored)
                    continue;
                if (!string.Equals(record.Kind, "registry", StringComparison.Ordinal)
                    || string.Equals(record.Hive, "LocalMachine", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // 新旧格式均只认 C# 自己的前缀；旧的通用前缀再用 JSON 数组守卫，避免误读 PowerShell 文档。
    internal static bool IsCSharpBackupFile(string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith(CSharpBackupPrefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(V2BackupPrefix, StringComparison.OrdinalIgnoreCase))
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

        /// <summary>内容为空或无法解析、永远不可能再产生还原的备份：已改名 .stale 归档（一次性告知，不再反复报失败）。</summary>
        public List<string> ArchivedStale { get; } = new();
    }

    private static IReadOnlyList<BackupRecord> CreateBackupRecords(string id, string? gamePath)
    {
        switch (id)
        {
            case "power-ultimate":
                return new[] { new BackupRecord { Id = id, Kind = "power-plan", OldActiveGuid = NativePowerSettings.RequireActiveGuid(), CreatedPlanGuid = Guid.NewGuid().ToString("D") } };
            case "power-tuning":
            {
                var plan = NativePowerSettings.RequireActiveGuid();
                return new[] { new BackupRecord { Id = id, Kind = "power-tuning", TargetPlanGuid = plan,
                    OldUsbValue = NativePowerSettings.ReadAc(plan, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
                    OldBoostValue = NativePowerSettings.ReadAc(plan, "54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7") } };
            }
            case "pcie-aspm-off":
            {
                var plan = NativePowerSettings.RequireActiveGuid();
                return new[] { new BackupRecord { Id = id, Kind = "power-aspm", TargetPlanGuid = plan,
                    OldAspmValue = NativePowerSettings.ReadAc(plan, "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5") } };
            }
            case "nic-power-save-off":
                return CaptureNicPowerSaveBackups(id);
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
                        OldStartMode = NativeSystem.GetServiceStartMode(serviceName),
                        // 记录应用前的运行状态（RUNNING 等），还原启动类型后据此拉回运行
                        OldState = NativeSystem.GetServiceState(serviceName)
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
            case "mouse-latency":
                return new[] { CreateRegistryBackup(id, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", RegistryValueKind.DWord) };
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
        var snapshot = RegistryHelper.ReadSnapshot(hive, path, name);
        var exists = snapshot.Existed;
        if (exists && snapshot.Kind != RegistryValueKind.DWord) throw new InvalidOperationException("Unexpected registry value type.");
        record.SecondaryHive = hive.ToString();
        record.SecondaryPath = path;
        record.SecondaryName = name;
        record.SecondaryExisted = exists;
        record.SecondaryValue = snapshot.Value;
    }

    private static BackupRecord CreateRegistryBackup(
        string id, RegistryHive hive, string path, string name, RegistryValueKind kind)
    {
        var snapshot = RegistryHelper.ReadSnapshot(hive, path, name);
        var exists = snapshot.Existed;
        if (exists && snapshot.Kind != kind) throw new InvalidOperationException("Unexpected registry value type.");
        var old = snapshot.Value;
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

    // nic-power-save-off 逐物理网卡备份：每块待改的网卡一条 registry 记录
    //（PnPCapabilities 已含 0x18 位的不改也不备份）；原值不存在时 Existed=false，
    // 还原走删除路径。与 ApplyNicPowerSaveOff 的过滤口径（NCF_VIRTUAL/DriverDesc）保持一致。
    private static IReadOnlyList<BackupRecord> CaptureNicPowerSaveBackups(string id)
    {
        const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        var records = new List<BackupRecord>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var classKey = baseKey.OpenSubKey(classPath);
        if (classKey is null)
            return records;

        foreach (var sub in classKey.GetSubKeyNames())
        {
            using var key = classKey.OpenSubKey(sub);
            if (key is null) continue;
            if (key.GetValue("Characteristics") is int caps && (caps & 0x1) != 0) continue;
            if (key.GetValue("DriverDesc") is not string) continue;
            var current = key.GetValue("PnPCapabilities");
            if (current is int v && (v & 0x18) == 0x18) continue;
            records.Add(new BackupRecord
            {
                Id = id,
                Kind = "registry",
                Hive = RegistryHive.LocalMachine.ToString(),
                Path = classPath + "\\" + sub,
                Name = "PnPCapabilities",
                OldValue = current,
                ValueKind = RegistryValueKind.DWord.ToString(),
                Existed = current is not null
            });
        }
        return records;
    }

}
