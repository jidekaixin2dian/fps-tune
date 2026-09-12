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
    private static bool TryLoadRestoreJournal(out RestoreInFlight? marker, out string? error)
    {
        marker = null;
        error = null;
        try
        {
            var path = RestoreJournalPath();
            if (Directory.Exists(path))
            {
                error = "未决标记路径不是文件";
                return false;
            }
            if (!File.Exists(path))
                return true;

            marker = JsonSerializer.Deserialize<RestoreInFlight>(File.ReadAllText(path));
            if (marker is null
                || !IsSafeBackupFileName(marker.BackupFile)
                || !IsSafeRecordId(marker.RecordId)
                || marker.RecordIndex < 0
                || !IsSha256(marker.RecordFingerprint))
            {
                marker = null;
                error = "未决标记字段不合法";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = SafeRestoreError(ex);
            return false;
        }
    }

    private static string RestoreJournalPath()
    {
        var dir = Path.GetFullPath(BackupDir);
        var path = Path.GetFullPath(Path.Combine(dir, RestoreJournalFileName));
        if (!string.Equals(Path.GetDirectoryName(path), dir, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path), RestoreJournalFileName, StringComparison.Ordinal))
            throw new InvalidOperationException("还原未决标记路径不受信任");
        return path;
    }

    private static bool IsSafeBackupFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('\\')
            || name.Contains('/')
            || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.StartsWith(CSharpBackupPrefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(LegacyBackupPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeRecordId(string? id)
        => !string.IsNullOrWhiteSpace(id)
            && id.Length <= 128
            && !id.Contains('\\')
            && !id.Contains('/');

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    private static bool SameBackupPath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeBackupFilePath(string file)
    {
        try
        {
            var dir = Path.GetFullPath(BackupDir);
            var full = Path.GetFullPath(file);
            return string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)
                && IsSafeBackupFileName(Path.GetFileName(full));
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRestoreJournal(string file, int recordIndex, BackupRecord record)
    {
        if (recordIndex < 0 || !IsSafeBackupFilePath(file))
            throw new InvalidOperationException("还原目标文件不受信任");

        var marker = new RestoreInFlight
        {
            BackupFile = Path.GetFileName(file),
            RecordIndex = recordIndex,
            RecordId = record.Id,
            RecordFingerprint = RecordFingerprint(record)
        };
        var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
        WriteDurableAtomicText(RestoreJournalPath(), json);
    }

    private static void PersistRestoreProgress(string file, string json)
    {
        if (!IsSafeBackupFilePath(file))
            throw new InvalidOperationException("还原进度目标文件不受信任");

        if (RestoreProgressWriteOverride is not null)
            RestoreProgressWriteOverride(file, json);
        else
            WriteDurableAtomicText(file, json);
    }

    private static void WriteDurableAtomicText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("还原未决标记目录不可用");
        Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (Directory.Exists(path))
                throw new IOException("还原未决标记目标不是文件");
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // 本次写入异常优先；残留临时文件仍在受限的 BackupDir 内。
            }
        }
    }

    private static void ClearRestoreJournal()
    {
        var path = RestoreJournalPath();
        if (Directory.Exists(path))
            throw new IOException("还原未决标记目标不是文件");
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string RecordFingerprint(BackupRecord record)
    {
        var restored = record.Restored;
        try
        {
            // Restored 是进度位，不参与指纹；成功持久化后仍应能核对并清理旧标记。
            record.Restored = false;
            var json = JsonSerializer.Serialize(record);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
        finally
        {
            record.Restored = restored;
        }
    }

    private static string SafeRestoreError(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : PrivacyScrub.Sanitize(ex.Message);

    // 定向还原的过滤决策（纯函数，便于单测）：
    // 已标记还原的记录永不重复执行；有选择时只留所选待还原记录。
}
