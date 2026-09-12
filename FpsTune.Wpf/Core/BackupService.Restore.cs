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
    /// <summary>
    /// 遍历全部备份文件逐项还原；失败的项会如实报告，不会静默吞掉。
    /// 传入 <paramref name="ids"/> 时只还原指定项（A/B 实验语义）：
    /// 文件中其余未选中的记录保持不动；已成功的记录会持久化标记，防止重复还原。
    /// 传入 <paramref name="onlyBackupFile"/> 时只处理这一份备份文件——调用方（A/B 实验）
    /// 必须锚定"本步骤自己创建的那份快照"，否则会把更早备份里的值当成本步骤的原值。
    /// </summary>
    public static RestoreAllResult RestoreAll(
        IReadOnlyCollection<string>? ids = null, string? onlyBackupFile = null)
    {
        Mutex? mutex = null;
        var acquired = false;
        var abandoned = false;
        RestoreAllResult? result = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name: RestoreMutexName);
            try
            {
                acquired = mutex.WaitOne(RestoreMutexWaitTimeoutOverride ?? RestoreMutexWaitTimeout);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne 发生 AbandonedMutexException 时所有权已经交给当前调用；
                // 继续由 journal 守卫接管，但把异常退出事实报告给调用方。
                acquired = true;
                abandoned = true;
            }

            if (!acquired)
            {
                result = new RestoreAllResult();
                result.Failures.Add("等待还原互斥超时，未执行任何还原；请稍后重试。");
                return result;
            }

            result = RestoreAllCore(ids, onlyBackupFile);
            if (abandoned)
                result.Failures.Insert(0, "检测到上次还原进程异常退出，已接管互斥；请核对还原结果与未决标记。");
            return result;
        }
        catch (AbandonedMutexException)
        {
            // 某些运行时可能在外层传播遗弃异常；此时同样不应继续无锁写入。
            result ??= new RestoreAllResult();
            result.Failures.Add("还原互斥被遗弃且无法安全接管，未执行还原；请人工核实。");
            return result;
        }
        catch (Exception ex)
        {
            result ??= new RestoreAllResult();
            result.Failures.Add("还原互斥初始化失败，未执行还原：" + PrivacyScrub.Sanitize(ex.Message));
            return result;
        }
        finally
        {
            if (acquired && mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    result?.Failures.Add("还原互斥释放失败：" + PrivacyScrub.Sanitize(ex.Message));
                }
            }
            mutex?.Dispose();
        }
    }

    private static RestoreAllResult RestoreAllCore(
        IReadOnlyCollection<string>? ids = null, string? onlyBackupFile = null)
    {
        HashSet<string>? selection = null;
        if (ids is not null && ids.Count > 0)
            selection = new HashSet<string>(ids, StringComparer.Ordinal);

        Directory.CreateDirectory(BackupDir);
        var result = new RestoreAllResult();
        RestoreInFlight? inFlight;
        if (!TryLoadRestoreJournal(out inFlight, out var journalError))
        {
            result.Failures.Add("还原未决标记读取失败：" + journalError + "；需人工核实，已跳过自动还原。");
            return result;
        }

        var allFiles = Directory.GetFiles(BackupDir, "*.json")
            .Where(IsCSharpBackupFile)
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();

        // 定向锚定：只允许还原调用方明确指定的那一份备份，且必须位于本机备份目录内。
        // 用户可写目录里的任意路径都不能成为还原目标。
        string? anchoredFile = null;
        if (onlyBackupFile is not null)
        {
            if (string.IsNullOrWhiteSpace(onlyBackupFile)
                || !Path.IsPathFullyQualified(onlyBackupFile)
                || !IsSafeBackupFilePath(onlyBackupFile))
            {
                result.Failures.Add("指定的备份文件不合法（必须是本机备份目录内的备份文件），未执行任何还原。");
                return result;
            }

            anchoredFile = Path.GetFullPath(onlyBackupFile);
            if (!File.Exists(anchoredFile))
            {
                result.Failures.Add(
                    $"指定的备份文件不存在（可能已被还原或改名）：{Path.GetFileName(anchoredFile)}");
                return result;
            }

            if (!allFiles.Any(file => SameBackupPath(file, anchoredFile)))
            {
                result.Failures.Add(
                    $"指定的文件不是可还原的 FpsTune 备份（命名或内容不匹配）：{Path.GetFileName(anchoredFile)}");
                return result;
            }
        }

        if (inFlight is { } marker)
        {
            var markerPath = Path.Combine(BackupDir, marker.BackupFile);
            var markerFile = allFiles.FirstOrDefault(file => string.Equals(
                Path.GetFileName(file), marker.BackupFile, StringComparison.OrdinalIgnoreCase));
            if (!IsSafeBackupFilePath(markerPath) || markerFile is null)
            {
                result.Failures.Add(
                    $"还原未决标记指向未知或不存在的备份文件「{marker.BackupFile}」；需人工核实，已跳过自动还原。");
                return result;
            }

            List<BackupRecord>? markerRecords;
            try
            {
                markerRecords = JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(markerFile));
            }
            catch (Exception ex)
            {
                result.Failures.Add(
                    $"{marker.BackupFile}: 未决还原标记无法核对（{ex.Message}）；需人工核实，已跳过自动还原。");
                return result;
            }

            if (markerRecords is null
                || marker.RecordIndex < 0
                || marker.RecordIndex >= markerRecords.Count)
            {
                result.Failures.Add(
                    $"{marker.BackupFile} / {marker.RecordId}: 未决还原标记与备份记录不一致；需人工核实，已跳过自动还原。");
                return result;
            }

            var markedRecord = markerRecords[marker.RecordIndex];
            if (markedRecord is null
                || !string.Equals(markedRecord.Id, marker.RecordId, StringComparison.Ordinal)
                || !string.Equals(RecordFingerprint(markedRecord), marker.RecordFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add(
                    $"{marker.BackupFile} / {marker.RecordId}: 未决还原标记与备份记录不一致；需人工核实，已跳过自动还原。");
                return result;
            }

            if (!markedRecord.Restored)
            {
                // 这是系统写入结果不确定的 durable marker；必须在读取/写入任何其他备份记录前停止，
                // 且绝不能让其他文件的成功项清掉它，否则下一次会重复覆盖用户后续修改。
                result.Failures.Add(
                    $"{marker.BackupFile} / {marker.RecordId}: 检测到上次未决还原，系统写入结果不确定；需人工核实，已跳过且绝不重复还原。");
                return result;
            }

            try
            {
                // 只有 marker 指向的记录已经成功持久化 Restored，才允许清除 marker。
                ClearRestoreJournal();
                inFlight = null;
            }
            catch (Exception ex)
            {
                result.Failures.Add(
                    $"{marker.BackupFile} / {marker.RecordId}: 还原已持久化但未决标记清理失败（{ex.Message}）；需人工核实，已跳过自动还原。");
                return result;
            }
        }

        var files = anchoredFile is null
            ? allFiles
            : allFiles.Where(file => SameBackupPath(file, anchoredFile)).ToList();

        var consumed = new List<string>();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            List<BackupRecord>? records;
            try
            {
                records = JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{fileName}: 备份文件无法解析（{ex.Message}）");
                continue;
            }

            if (records is null)
            {
                result.Failures.Add($"{fileName}: 备份内容为空");
                continue;
            }

            if (records.Count == 0)
            {
                result.Failures.Add($"{fileName}: 备份内容为空");
                continue;
            }

            var fileFailed = false;

            // 先校验尚未还原的记录，避免后面的篡改记录导致前面的记录先写入系统。
            foreach (var record in records)
            {
                if (record is null)
                {
                    fileFailed = true;
                    result.Failures.Add($"{Path.GetFileName(file)}: 备份包含空记录");
                    continue;
                }
                if (record.Restored)
                    continue;

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

            // 按选择过滤：已还原的记录不再执行，文件里还有未选中的待还原记录时不消费。
            var (targets, _) = FilterRecords(records, selection);
            if (targets.Count == 0)
            {
                // 兼容早期部分还原留下的全已还原 JSON：补做审计归档。
                if (records.All(r => r.Restored))
                    consumed.Add(file);
                continue;
            }

            foreach (var record in targets)
            {
                var recordIndex = records.IndexOf(record);
                try
                {
                    WriteRestoreJournal(file, recordIndex, record);
                }
                catch (Exception ex)
                {
                    fileFailed = true;
                    result.Failures.Add(
                        $"{fileName} / {record.Id}: 还原前进度标记无法保存（{ex.Message}），未写入系统。");
                    return result;
                }

                try
                {
                    if (RestoreRecordOverride is not null)
                        RestoreRecordOverride(record);
                    else
                        RestoreOne(record);
                    record.Restored = true;
                    result.Restored.Add((file, record.Id));

                    // 每项都先持久化 Restored，再清理未决标记；任一步失败都留下标记，
                    // 下次只报告人工核实，绝不再次覆盖系统值。
                    var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                    PersistRestoreProgress(file, json);
                    ClearRestoreJournal();
                }
                catch (Exception ex)
                {
                    fileFailed = true;
                    result.Failures.Add(
                        $"{fileName} / {record.Id}: 还原后状态无法确定（{ex.Message}）；需人工核实，未再次尝试。");
                    // journal 仍指向本记录；本批次必须立即停止，避免后续文件清掉 marker。
                    return result;
                }
            }

            // 只有全部待还原记录成功后才消费为 .restored；部分成功保留 JSON 及逐条状态。
            if (!fileFailed && !records.Any(r => !r.Restored))
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
        // 只还原备份里实际读到的原值；读不到（null）说明该设置从未被应用或平台不支持，跳过。
        // 旧版本备份的 boost 查询用的是错误 GUID（必然为 null），更不能按默认值强写。
        if (r.OldUsbValue.HasValue)
            SetAcValue("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", r.OldUsbValue.Value);
        if (r.OldBoostValue.HasValue)
            SetAcValue("54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7", r.OldBoostValue.Value);
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
