using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using FpsTune.Wpf.Services;
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

    // pcie-aspm-off 的原始 AC 值；null 表示当时读取失败/平台不支持，还原时跳过该设置。
    public int? OldAspmValue { get; set; }

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

    // 定向还原后保留原始快照供审计，同时防止同一记录被后续 RestoreAll 重复覆盖。
    public bool Restored { get; set; }
}

public static partial class BackupService
{
    private const string CSharpBackupPrefix = "csharp-backup-";
    private const string LegacyBackupPrefix = "backup-";
    private const string AutostartBackupId = "autostart";
    private const string AutostartRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartRunValueName = "FpsTune";
    private const string RestoreJournalFileName = ".restore-inflight.json";
    internal const string RestoreMutexName = @"Local\FpsTune.RestoreAll";
    private static readonly TimeSpan RestoreMutexWaitTimeout = TimeSpan.FromSeconds(30);

    // 测试可注入；生产代码保持默认目录
    internal static string? BackupDirOverride { get; set; }
    internal static Action<BackupRecord>? RestoreRecordOverride { get; set; }
    internal static Action<string, string>? RestoreProgressWriteOverride { get; set; }
    internal static TimeSpan? RestoreMutexWaitTimeoutOverride { get; set; }

    private sealed class RestoreInFlight
    {
        public string BackupFile { get; set; } = "";
        public int RecordIndex { get; set; }
        public string RecordId { get; set; } = "";
        public string RecordFingerprint { get; set; } = "";
    }

    private static string BackupDir =>
        BackupDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "backup");

    /// <summary>0.2.2 C-B：把全部备份文件（含 .restored 审计）打包导出为 zip，返回导出的文件数。</summary>
    public static int ExportBackups(string zipPath)
    {
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        using var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(BackupDir, "*")
                     .Where(file => IsBackupTransferName(Path.GetFileName(file)) && IsCSharpBackupFile(file)))
        {
            archive.CreateEntryFromFile(file, Path.GetFileName(file));
            count++;
        }
        return count;
    }

    /// <summary>0.2.2 C-B：从 zip 导入备份。逐条过 ValidateRecord 校验（目标必须在 Capture 白名单内），
    /// 同名文件跳过（重复导入幂等），校验失败的单文件跳过并计数。返回 (导入, 跳过重复, 校验失败)。</summary>
    public static (int Imported, int SkippedDuplicate, int SkippedInvalid) ImportBackups(string zipPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        int imported = 0, dup = 0, invalid = 0;
        Directory.CreateDirectory(BackupDir);
        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.Name);
            if (!IsBackupTransferName(name))
                continue;
            if (File.Exists(Path.Combine(BackupDir, name)))
            {
                dup++;
                continue;
            }
            string json;
            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            catch
            {
                invalid++;
                continue;
            }
            try
            {
                var records = JsonSerializer.Deserialize<List<BackupRecord>>(json);
                if (records is null || records.Count == 0)
                    throw new InvalidOperationException("empty backup file");
                foreach (var r in records)
                    ValidateRecord(r);
            }
            catch
            {
                invalid++;
                continue;
            }
            var target = Path.Combine(BackupDir, name);
            try
            {
                using var stream = entry.Open();
                using var outFile = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.CopyTo(outFile);
                imported++;
            }
            catch (IOException) when (File.Exists(target))
            {
                dup++;   // 罕见并发冲突视为重复
            }
            catch
            {
                invalid++;
                try { File.Delete(target); } catch { }
            }
        }
        return (imported, dup, invalid);
    }

    private static bool IsBackupTransferName(string name) =>
        (name.StartsWith(CSharpBackupPrefix, StringComparison.OrdinalIgnoreCase)
         || name.StartsWith(LegacyBackupPrefix, StringComparison.OrdinalIgnoreCase))
        && (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".json.restored", StringComparison.OrdinalIgnoreCase));

}
