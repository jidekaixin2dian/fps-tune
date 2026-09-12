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
    internal static (IReadOnlyList<BackupRecord> Targets, bool HasUnselected) FilterRecords(
        IReadOnlyList<BackupRecord> records, HashSet<string>? selection)
    {
        var pending = records.Where(r => !r.Restored).ToList();
        if (selection is null)
            return (pending, false);

        var targets = pending.Where(r => selection.Contains(r.Id)).ToList();
        return (targets, targets.Count != pending.Count);
    }

    // 备份文件位于用户可写目录，还原前必须验证其目标。
    internal static void ValidateRecord(BackupRecord record)
    {
        if (record is null)
            throw new InvalidOperationException("备份记录为空");

        if (record.Id == AutostartBackupId)
        {
            EnsureKind(record, "registry");
            EnsureRegistryFields(record);
            Reject(!string.Equals(record.Hive, RegistryHive.CurrentUser.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.Path, AutostartRunKeyPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.Name, AutostartRunValueName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.ValueKind, RegistryValueKind.String.ToString(), StringComparison.Ordinal),
                "开机自启备份目标字段不匹配");
            EnsureNoSecondary(record);
            return;
        }

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

}
