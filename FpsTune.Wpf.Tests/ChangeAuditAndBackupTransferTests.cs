using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>0.2.2：M4 改动总览收集 + C-B 备份导出/导入（重装/换机找回优化前状态）。</summary>
[Collection("BackupService serial")]
public sealed class ChangeAuditAndBackupTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "fpstune-audit-tests-" + Guid.NewGuid().ToString("N"));

    public ChangeAuditAndBackupTransferTests()
    {
        Directory.CreateDirectory(_dir);
        BackupService.BackupDirOverride = _dir;
    }

    public void Dispose()
    {
        BackupService.BackupDirOverride = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteBackupFile(string name, params BackupRecord[] records)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path,
            JsonSerializer.Serialize(records.ToList(), new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void Import_rejects_high_compression_oversized_entry_before_writing()
    {
        var zip = Path.Combine(_dir, "oversized.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("csharp-backup-oversized.json").Open();
            stream.Write(new byte[10 * 1024 * 1024 + 1]);
        }
        Assert.Equal((0, 0, 1), BackupService.ImportBackups(zip));
        Assert.False(File.Exists(Path.Combine(_dir, "csharp-backup-oversized.json")));
    }

    private static BackupRecord KeyboardLatencyRecord(int oldValue) => new()
    {
        Id = "keyboard-latency",
        Kind = "registry",
        Hive = "LocalMachine",
        Path = @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters",
        Name = "KeyboardDataQueueSize",
        OldValue = oldValue,
        ValueKind = "DWord",
        Existed = oldValue >= 0,
    };

    [Fact]
    public void CollectPending_picks_latest_record_per_item_and_describes_backup()
    {
        WriteBackupFile("csharp-backup-20260930-100000-aaaa.json", KeyboardLatencyRecord(20));
        WriteBackupFile("csharp-backup-20260930-110000-bbbb.json", KeyboardLatencyRecord(20));

        var rows = ChangeAudit.CollectPending();

        var row = Assert.Single(rows);
        Assert.Equal("keyboard-latency", row.ItemId);
        Assert.Equal("键盘缓冲区扩容", row.ItemName);
        Assert.Contains("kbdclass", row.BackupSummary);
        Assert.True(row.OriginalExisted);
        Assert.True(row.BackedUpAt > DateTime.MinValue);
    }

    [Fact]
    public void CollectPending_reports_missing_original_value()
    {
        WriteBackupFile("csharp-backup-20260930-100000-cccc.json", KeyboardLatencyRecord(-1));

        var row = Assert.Single(ChangeAudit.CollectPending());
        Assert.False(row.OriginalExisted);
    }

    [Fact]
    public void CollectPending_skips_restored_and_unknown_items()
    {
        var restored = KeyboardLatencyRecord(20);
        restored.Restored = true;
        WriteBackupFile("csharp-backup-20260930-100000-dddd.json", restored);

        var unknown = KeyboardLatencyRecord(20);
        unknown.Id = "no-such-item";
        WriteBackupFile("csharp-backup-20260930-100100-eeee.json", unknown);

        Assert.Empty(ChangeAudit.CollectPending());
    }

    [Fact]
    public void ExportImport_roundtrip_recovers_backups()
    {
        var original = Path.Combine(_dir, "csharp-backup-20260930-100000-ffff.json");
        File.WriteAllText(original,
            JsonSerializer.Serialize(new List<BackupRecord> { KeyboardLatencyRecord(20) }),
            new UTF8Encoding(false));
        var zip = Path.Combine(Path.GetTempPath(), "fpstune-export-" + Guid.NewGuid().ToString("N") + ".zip");
        Assert.Equal(1, BackupService.ExportBackups(zip));

        // 清空本地（模拟重装），再导入
        Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);
        BackupService.BackupDirOverride = _dir;
        try
        {

        var (imported, dup, invalid) = BackupService.ImportBackups(zip);
        Assert.Equal(1, imported);
        Assert.Equal(0, dup);
        Assert.Equal(0, invalid);

        // 导入的记录过白名单校验且可被总览读取
        Assert.Single(BackupService.ListBackups());
        Assert.Single(ChangeAudit.CollectPending());

        // 重复导入幂等
        var second = BackupService.ImportBackups(zip);
        Assert.Equal((0, 1, 0), second);
        }
        finally { if (File.Exists(zip)) File.Delete(zip); }
    }

    [Fact]
    public void ExportImport_preserves_legacy_array_and_consumed_backups()
    {
        var legacy = WriteBackupFile("backup-20260824-101758.json", KeyboardLatencyRecord(20));
        var consumed = KeyboardLatencyRecord(30);
        consumed.Restored = true;
        var restored = WriteBackupFile("backup-20260824-101759.json.restored", consumed);
        File.WriteAllText(Path.Combine(_dir, "backup-powershell.json"), "{\"items\":[]}");
        File.WriteAllText(Path.Combine(_dir, "csharp-backup-inflight.tmp"), "temporary");
        var expectedLegacy = File.ReadAllBytes(legacy);
        var expectedRestored = File.ReadAllBytes(restored);
        var zip = Path.Combine(_dir, "transfer.zip");

        Assert.Equal(2, BackupService.ExportBackups(zip));
        File.Delete(legacy);
        File.Delete(restored);

        Assert.Equal((2, 0, 0), BackupService.ImportBackups(zip));
        Assert.Equal(expectedLegacy, File.ReadAllBytes(legacy));
        Assert.Equal(expectedRestored, File.ReadAllBytes(restored));
        Assert.Equal((0, 2, 0), BackupService.ImportBackups(zip));
        Assert.Equal(2, BackupService.ListBackupStatuses().Count);
    }

    [Fact]
    public void Import_validates_legacy_records_and_ignores_unrelated_restored_names()
    {
        var zip = Path.Combine(_dir, "invalid-transfer.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("backup-invalid.json").Open()))
                writer.Write("[{\"Id\":\"keyboard-latency\",\"Kind\":\"registry\",\"Hive\":\"LocalMachine\",\"Path\":\"SOFTWARE\\\\Evil\",\"Name\":\"Evil\"}]");
            using (var writer = new StreamWriter(archive.CreateEntry("settings.json.restored").Open()))
                writer.Write(JsonSerializer.Serialize(new[] { KeyboardLatencyRecord(20) }));
        }

        Assert.Equal((0, 0, 1), BackupService.ImportBackups(zip));
        Assert.False(File.Exists(Path.Combine(_dir, "backup-invalid.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json.restored")));
    }

    [Fact]
    public void Import_skips_files_failing_validation()
    {
        var zip = Path.Combine(_dir, "evil.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("csharp-backup-20260930-100000-ffff.json");
            using var w = new StreamWriter(entry.Open());
            w.Write(JsonSerializer.Serialize(new List<BackupRecord>
            {
                new()   // 目标不在 Capture 白名单内 → 校验拒绝
                {
                    Id = "keyboard-latency",
                    Kind = "registry",
                    Hive = "LocalMachine",
                    Path = @"SOFTWARE\Evil\Path",
                    Name = "Evil",
                    OldValue = 1,
                    ValueKind = "DWord",
                    Existed = true,
                },
            }));
        }

        var (imported, _, invalid) = BackupService.ImportBackups(zip);

        Assert.Equal(0, imported);
        Assert.Equal(1, invalid);
        Assert.Empty(BackupService.ListBackups());
    }
}
