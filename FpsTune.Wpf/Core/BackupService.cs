using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public sealed class BackupRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "registry";
    public string Hive { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public object? OldValue { get; set; }
    public string ValueKind { get; set; } = "DWord";

    // 第二个关联注册表值（如 game-mode 的 AllowAutoGameMode、dvr-off 的 AllowGameDVR 策略），
    // 用于无损还原；旧版本备份没有该字段（null）时还原保持不动。
    // power-tuning 三项隐藏电源设置的原始 AC 值；null 表示当时读取失败，还原时回退常见默认值。
    public int? OldUsbValue { get; set; }
    public int? OldBoostValue { get; set; }
    public int? OldIdleValue { get; set; }

    public string? SecondaryHive { get; set; }
    public string? SecondaryPath { get; set; }
    public string? SecondaryName { get; set; }
    public bool? SecondaryExisted { get; set; }
    public object? SecondaryValue { get; set; }
    public bool Existed { get; set; }
    public string? ServiceName { get; set; }
    public int? OldStartValue { get; set; }
    public string? OldStartMode { get; set; }
    public string? OldActiveGuid { get; set; }
    public string? OldState { get; set; }
}

public static class BackupService
{
    private const string CSharpBackupPrefix = "csharp-backup-";
    private const string LegacyBackupPrefix = "backup-";

    // 测试可注入；生产代码保持默认目录
    internal static string? BackupDirOverride { get; set; }

    private static string BackupDir =>
        BackupDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "backup");

    public static string Capture(IEnumerable<string> ids, string? gamePath)
    {
        var records = new List<BackupRecord>();
        foreach (var id in ids.Distinct())
            records.AddRange(CreateBackupRecords(id, gamePath));

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

    /// <summary>遍历全部备份文件逐项还原；失败的项会如实报告，不会静默吞掉。</summary>
    public static RestoreAllResult RestoreAll()
    {
        Directory.CreateDirectory(BackupDir);
        var files = Directory.GetFiles(BackupDir, "*.json")
            .Where(IsCSharpBackupFile)
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();

        var result = new RestoreAllResult();
        var consumed = new List<string>();

        foreach (var file in files)
        {
            List<BackupRecord>? records;
            try
            {
                records = JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{Path.GetFileName(file)}: 备份文件无法解析（{ex.Message}）");
                continue;
            }

            if (records is null)
            {
                result.Failures.Add($"{Path.GetFileName(file)}: 备份内容为空");
                continue;
            }

            if (records.Count == 0)
            {
                result.Failures.Add($"{Path.GetFileName(file)}: 备份内容为空");
                continue;
            }

            var fileFailed = false;

            // 先校验整份文件的所有目标，避免文件中后面的篡改记录导致前面的记录先写入系统。
            foreach (var record in records)
            {
                if (record is null)
                {
                    fileFailed = true;
                    result.Failures.Add($"{Path.GetFileName(file)}: 备份包含空记录");
                    continue;
                }

                try
                {
                    ValidateRecord(record);
                }
                catch (Exception ex)
                {
                    fileFailed = true;
                    result.Failures.Add($"{Path.GetFileName(file)} / {record.Id}: {ex.Message}");
                }
            }

            if (fileFailed)
                continue;

            foreach (var record in records)
            {
                try
                {
                    RestoreOne(record);
                    result.Restored.Add((file, record.Id));
                }
                catch (Exception ex)
                {
                    fileFailed = true;
                    result.Failures.Add($"{Path.GetFileName(file)} / {record.Id}: {ex.Message}");
                }
            }

            // 只有整份文件的每一条记录都成功，才允许消费文件；部分成功也必须保留原文件。
            if (!fileFailed)
                consumed.Add(file);
        }

        // 消费备份：重命名为 .restored（保留供审计），与 PowerShell 引擎保持一致。
        foreach (var file in consumed)
        {
            try
            {
                var renamed = file + ".restored";
                if (File.Exists(renamed))
                {
                    result.Failures.Add($"{Path.GetFileName(file)}: 目标 .restored 文件已存在，未覆盖");
                    continue;
                }
                File.Move(file, renamed);
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{Path.GetFileName(file)}: 还原后备份文件改名失败（{ex.Message}）");
            }
        }

        return result;
    }

    // 备份文件位于用户可写目录，还原前必须验证其目标。
    internal static void ValidateRecord(BackupRecord record)
    {
        if (record is null)
            throw new InvalidOperationException("备份记录为空");
        Reject(!ItemCatalog.All.Any(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal)),
            $"未知优化项: {record.Id}");

        switch (record.Id)
        {
            case "power-ultimate":
                EnsureNonRegistry(record, "power-plan", allowGuid: true);
                Reject(!Guid.TryParse(record.OldActiveGuid, out _), "原电源计划 GUID 不合法");
                break;
            case "power-tuning":
                EnsureNonRegistry(record, "power-tuning", allowPower: true);
                break;
            case "sysmain-off":
            case "wsearch-off":
                EnsureNonRegistry(record, "service", allowService: true);
                var service = record.Id == "sysmain-off" ? "SysMain" : "WSearch";
                Reject(!string.Equals(record.ServiceName, service, StringComparison.Ordinal) ||
                    record.OldStartValue is < 0 or > 4 ||
                    (record.OldStartMode is not null &&
                     record.OldStartMode is not ("boot" or "system" or "auto" or "demand" or "disabled")),
                    "服务还原目标字段不匹配");
                break;
            case "hibernate-off":
                EnsureNonRegistry(record, "hibernate", allowState: true, allowedStates: new[] { "on", "off" });
                break;
            case "dyntick-off":
                EnsureNonRegistry(record, "bcdedit", allowState: true, allowedStates: new[] { "absent", "no", "yes" });
                break;
            default:
                EnsureKind(record, "registry");
                ValidateRegistryRecord(record);
                break;
        }
    }

    private static void ValidateRegistryRecord(BackupRecord record)
    {
        EnsureRegistryFields(record);
        if (record.Id == "gpu-pstate-lock")
        {
            EnsureRegistryShape(record, RegistryHive.LocalMachine, "DisableDynamicPstate", RegistryValueKind.DWord);
            Reject(!Regex.IsMatch(record.Path,
                @"^SYSTEM\\CurrentControlSet\\Control\\Class\\\{4d36e968-e325-11ce-bfc1-08002be10318\}\\00\d\d$",
                RegexOptions.IgnoreCase), "GPU 驱动注册表目标不匹配");
            EnsureNoSecondary(record);
            return;
        }

        if (record.Id == "net-nagle-off")
        {
            EnsureRegistryShape(record, RegistryHive.LocalMachine, "TcpAckFrequency", RegistryValueKind.DWord);
            const string prefix = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
            var name = record.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? record.Path[prefix.Length..] : "";
            Reject(!name.StartsWith("{", StringComparison.Ordinal) || !name.EndsWith("}", StringComparison.Ordinal) ||
                !Guid.TryParse(name[1..^1], out _), "网卡接口注册表目标不匹配");
            EnsureSecondary(record, RegistryHive.LocalMachine, record.Path, "TCPNoDelay");
            return;
        }

        string? gamePath = null;
        if (record.Id is "fso-off" or "gpu-pref")
        {
            EnsureExecutablePath(record.Name);
            gamePath = record.Name;
        }
        else if (record.Id == "game-priority")
        {
            const string prefix = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
            const string suffix = @"\PerfOptions";
            Reject(!record.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !record.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase),
                "游戏优先级注册表目标不匹配");
            var name = record.Path[prefix.Length..^suffix.Length];
            EnsureExecutableFileName(name);
            gamePath = @"C:\" + name;
        }

        Reject(!CreateBackupRecords(record.Id, gamePath).Any(candidate => SameTarget(record, candidate)),
            "备份注册表目标不在 Capture 白名单中");
    }

    private static bool SameTarget(BackupRecord actual, BackupRecord expected)
        => string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) &&
           string.Equals(actual.Kind, expected.Kind, StringComparison.Ordinal) &&
           string.Equals(actual.Hive, expected.Hive, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(actual.Path, expected.Path, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(actual.ValueKind, expected.ValueKind, StringComparison.Ordinal) &&
           actual.SecondaryExisted.HasValue == expected.SecondaryExisted.HasValue &&
           string.Equals(actual.SecondaryHive, expected.SecondaryHive, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(actual.SecondaryPath, expected.SecondaryPath, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(actual.SecondaryName, expected.SecondaryName, StringComparison.OrdinalIgnoreCase) &&
           (actual.SecondaryValue is null || actual.SecondaryExisted == true);

    private static void EnsureKind(BackupRecord record, string expected)
        => Reject(!string.Equals(record.Kind, expected, StringComparison.Ordinal),
            $"备份类型与优化项不匹配: {record.Kind}");

    private static void EnsureNonRegistry(BackupRecord record, string kind, bool allowPower = false,
        bool allowService = false, bool allowGuid = false, bool allowState = false,
        params string[] allowedStates)
    {
        EnsureKind(record, kind);
        Reject(!string.IsNullOrEmpty(record.Hive) || !string.IsNullOrEmpty(record.Path) ||
            !string.IsNullOrEmpty(record.Name) || record.OldValue is not null || record.Existed ||
            record.SecondaryExisted.HasValue || !string.IsNullOrEmpty(record.SecondaryHive) ||
            !string.IsNullOrEmpty(record.SecondaryPath) || !string.IsNullOrEmpty(record.SecondaryName) ||
            record.SecondaryValue is not null ||
            (!allowPower && (record.OldUsbValue.HasValue || record.OldBoostValue.HasValue || record.OldIdleValue.HasValue)) ||
            (!allowService && (!string.IsNullOrEmpty(record.ServiceName) || record.OldStartValue.HasValue ||
                               !string.IsNullOrEmpty(record.OldStartMode))) ||
            (!allowGuid && !string.IsNullOrEmpty(record.OldActiveGuid)) ||
            (!allowState && !string.IsNullOrEmpty(record.OldState)) ||
            (allowState && (record.OldState is null || !allowedStates.Contains(record.OldState, StringComparer.Ordinal))),
            "非注册表备份包含不匹配的目标字段");
    }

    private static void EnsureRegistryFields(BackupRecord record)
        => Reject(record.OldUsbValue.HasValue || record.OldBoostValue.HasValue || record.OldIdleValue.HasValue ||
            !string.IsNullOrEmpty(record.ServiceName) || record.OldStartValue.HasValue ||
            !string.IsNullOrEmpty(record.OldStartMode) || !string.IsNullOrEmpty(record.OldActiveGuid) ||
            !string.IsNullOrEmpty(record.OldState), "注册表备份包含其他类型字段");

    private static void EnsureRegistryShape(BackupRecord record, RegistryHive hive, string name, RegistryValueKind kind)
        => Reject(!string.Equals(record.Hive, hive.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.ValueKind, kind.ToString(), StringComparison.Ordinal),
            "注册表目标字段不匹配");

    private static void EnsureSecondary(BackupRecord record, RegistryHive hive, string path, string name)
        => Reject(!string.Equals(record.SecondaryHive, hive.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.SecondaryPath, path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.SecondaryName, name, StringComparison.OrdinalIgnoreCase) ||
            !record.SecondaryExisted.HasValue ||
            (!record.SecondaryExisted.Value && record.SecondaryValue is not null), "关联注册表目标字段不匹配");

    private static void EnsureNoSecondary(BackupRecord record)
        => Reject(record.SecondaryExisted.HasValue || !string.IsNullOrEmpty(record.SecondaryHive) ||
            !string.IsNullOrEmpty(record.SecondaryPath) || !string.IsNullOrEmpty(record.SecondaryName) ||
            record.SecondaryValue is not null, "备份包含不匹配的关联字段");

    private static void EnsureExecutablePath(string path)
    {
        Reject(!Path.IsPathFullyQualified(path) || path.Contains('\0'), "游戏可执行文件目标不合法");
        EnsureExecutableFileName(Path.GetFileName(path));
    }

    private static void EnsureExecutableFileName(string name)
        => Reject(string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/') ||
            !string.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase) || name.Contains('\0'),
            "游戏可执行文件目标不合法");

    private static void Reject(bool invalid, string message)
    {
        if (invalid)
            throw new InvalidOperationException(message);
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
                            "be337238-0d82-4146-a960-4f3749d470c7", "45bcc044-d885-43e2-8605-ee0ec6e96b59"),
                        OldIdleValue = GetPowerAcIndex(
                            "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d", "4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3")
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

    private static void RestoreOne(BackupRecord r)
    {
        ValidateRecord(r);
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
                RestorePowerTuning(r);
                break;
            case "hibernate":
                RestoreHibernate(r);
                break;
            case "bcdedit":
                RestoreBcdedit(r);
                break;
            default:
                throw new InvalidOperationException("不支持的备份记录类型: " + r.Kind);
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

        // 第二个关联值（AllowAutoGameMode / AllowGameDVR 策略）按原值恢复；
        // 旧版本备份没有 SecondaryExisted 字段时保持不动，避免覆盖或误删用户原有设置。
        if (r.SecondaryExisted.HasValue && !string.IsNullOrEmpty(r.SecondaryHive))
        {
            var secHive = Enum.Parse<RegistryHive>(r.SecondaryHive);
            if (r.SecondaryExisted.Value)
                RegistryHelper.SetValue(secHive, r.SecondaryPath!, r.SecondaryName!,
                    ConvertValue(r.SecondaryValue ?? 0, RegistryValueKind.DWord), RegistryValueKind.DWord);
            else
                RegistryHelper.DeleteValue(secHive, r.SecondaryPath!, r.SecondaryName!);
        }
    }

    private static void RestoreService(BackupRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.ServiceName))
            throw new InvalidOperationException("备份缺少服务名称");

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
        EnsureNativeSuccess(
            NativeSystem.Run("sc.exe", "config", r.ServiceName, "start=", mode),
            $"还原 {r.ServiceName} 启动类型");
    }

    private static void RestorePowerPlan(BackupRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.OldActiveGuid))
            throw new InvalidOperationException("备份缺少原电源计划 GUID");

        EnsureNativeSuccess(
            NativeSystem.Run("powercfg.exe", "-setactive", r.OldActiveGuid),
            "还原原电源计划");
    }

    private static void RestorePowerTuning(BackupRecord r)
    {
        // 优先还原备份的原值；旧版本备份没有数据时回退到常见默认值。
        SetAcValue("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", r.OldUsbValue ?? 1);
        SetAcValue("be337238-0d82-4146-a960-4f3749d470c7", "45bcc044-d885-43e2-8605-ee0ec6e96b59", r.OldBoostValue ?? 0);
        SetAcValue("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d", "4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3", r.OldIdleValue ?? 1);
        EnsureNativeSuccess(
            NativeSystem.Run("powercfg.exe", "-setactive", "SCHEME_CURRENT"),
            "重新应用电源计划");
    }

    private static void SetAcValue(string subgroup, string setting, int value)
        => EnsureNativeSuccess(
            NativeSystem.Run("powercfg.exe", "-setacvalueindex", "SCHEME_CURRENT", subgroup, setting, value.ToString()),
            "还原电源隐藏项");

    // 查询某电源设置的当前 AC 值（失败返回 null），用于 power-tuning 无损备份。
    private static int? GetPowerAcIndex(string subgroup, string setting)
    {
        var r = NativeSystem.Run("powercfg.exe", "/query", "SCHEME_CURRENT", subgroup, setting);
        if (!r.Success)
            return null;

        foreach (var line in r.Output.Split('\n'))
        {
            if (!line.Contains("AC", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("交流", StringComparison.Ordinal))
                continue;

            var m = Regex.Match(line, @"0x([0-9a-fA-F]+)");
            if (m.Success && int.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber, null, out var value))
                return value;
        }

        return null;
    }

    private static void RestoreHibernate(BackupRecord r)
    {
        switch (r.OldState?.ToLowerInvariant())
        {
            case "on":
                EnsureNativeSuccess(NativeSystem.Run("powercfg.exe", "/h", "on"), "重新开启休眠");
                break;
            case "off":
                break;
            default:
                throw new InvalidOperationException("备份缺少有效的休眠状态");
        }
    }

    private static void RestoreBcdedit(BackupRecord r)
    {
        switch (r.OldState?.ToLowerInvariant())
        {
            case "yes":
                EnsureNativeSuccess(
                    NativeSystem.Run("bcdedit.exe", "/set", "{current}", "disabledynamictick", "yes"),
                    "还原 disabledynamictick=yes");
                break;
            case "no":
                EnsureNativeSuccess(
                    NativeSystem.Run("bcdedit.exe", "/set", "{current}", "disabledynamictick", "no"),
                    "还原 disabledynamictick=no");
                break;
            case "absent":
                EnsureNativeSuccess(
                    NativeSystem.Run("bcdedit.exe", "/deletevalue", "{current}", "disabledynamictick"),
                    "删除 disabledynamictick");
                break;
            default:
                throw new InvalidOperationException("备份缺少有效的 disabledynamictick 状态");
        }
    }

    private static void EnsureNativeSuccess(NativeResult result, string operation)
    {
        if (!result.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? $"退出码 {result.ExitCode}" : result.Error.Trim();
            throw new InvalidOperationException($"{operation}失败：{detail}");
        }
    }

    internal static object ConvertValue(object value, RegistryValueKind kind)
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
            "prio-separation" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", RegistryValueKind.DWord),
            "wer-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", RegistryValueKind.DWord),
            "transparency-off" => (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", RegistryValueKind.DWord),
            "mpo-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", RegistryValueKind.DWord),
            "net-throttling-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", RegistryValueKind.DWord),
            "sys-responsiveness" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", RegistryValueKind.DWord),
            "paging-exec" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", RegistryValueKind.DWord),
            "mem-compress-off" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCompression", RegistryValueKind.DWord),
            _ => null
        };
    }
}
