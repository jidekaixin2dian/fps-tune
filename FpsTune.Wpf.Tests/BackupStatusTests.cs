using System.Text.Json;
using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 备份页文件状态列表（P2-11）的引擎侧守卫：
/// .json = 待还原，*.json.restored = 已消费的审计文件，坏文件如实标"无法读取"，
/// 非备份文件绝不混入。全部走 BackupDirOverride 指向的临时目录，不碰真实备份。
/// </summary>
[Collection("BackupService serial")]
public sealed class BackupStatusTests : IDisposable
{
    private readonly string _dir;

    public BackupStatusTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        BackupService.BackupDirOverride = _dir;
    }

    public void Dispose()
    {
        BackupService.BackupDirOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
    }

    private static string WriteIn(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Records(params BackupRecord[] records)
        => JsonSerializer.Serialize(records);

    [Fact]
    public void Counts_pending_and_consumed_records_and_marks_invalid_files()
    {
        WriteIn(_dir, "csharp-backup-a.json", Records(
            new BackupRecord { Id = "a1" },
            new BackupRecord { Id = "a2", Restored = true }));
        WriteIn(_dir, "csharp-backup-b.json.restored", Records(
            new BackupRecord { Id = "b1", Restored = true },
            new BackupRecord { Id = "b2", Restored = true }));
        WriteIn(_dir, "csharp-backup-bad.json", "{ 这不是 JSON");
        WriteIn(_dir, "csharp-backup-c.json", "[]");
        WriteIn(_dir, "unrelated.json", "[]");

        var list = BackupService.ListBackupStatuses();

        // unrelated.json 没有备份前缀，不进入状态列表
        Assert.Equal(4, list.Count);

        var a = Assert.Single(list, s => s.FileName == "csharp-backup-a.json");
        Assert.True(a.Valid);
        Assert.Equal(1, a.PendingCount);
        Assert.Equal(1, a.RestoredCount);

        var b = Assert.Single(list, s => s.FileName == "csharp-backup-b.json.restored");
        Assert.True(b.Valid);
        Assert.Equal(0, b.PendingCount);
        Assert.Equal(2, b.RestoredCount);

        var bad = Assert.Single(list, s => s.FileName == "csharp-backup-bad.json");
        Assert.False(bad.Valid);

        var c = Assert.Single(list, s => s.FileName == "csharp-backup-c.json");
        Assert.True(c.Valid);
        Assert.Equal(0, c.PendingCount);
        Assert.Equal(0, c.RestoredCount);

        Assert.DoesNotContain(list, s => s.FileName == "unrelated.json");
    }

    [Fact]
    public void Orders_by_last_write_descending()
    {
        var old1 = WriteIn(_dir, "csharp-backup-old.json", Records(new BackupRecord { Id = "o" }));
        var new1 = WriteIn(_dir, "csharp-backup-new.json.restored", Records(new BackupRecord { Id = "n", Restored = true }));
        File.SetLastWriteTime(old1, new DateTime(2026, 1, 1, 8, 0, 0));
        File.SetLastWriteTime(new1, new DateTime(2026, 1, 2, 8, 0, 0));

        var list = BackupService.ListBackupStatuses();

        Assert.Equal(2, list.Count);
        Assert.Equal("csharp-backup-new.json.restored", list[0].FileName);
        Assert.Equal("csharp-backup-old.json", list[1].FileName);
    }

    [Fact]
    public void Real_capture_and_restore_produce_the_documented_states()
    {
        // 走真实 Capture → RestoreAll（拦截注册表写入）路径：
        // 待还原 .json → 全部记录标记 Restored 并持久化 → 消费为 .json.restored
        var consumed = new List<string>();
        BackupService.RestoreRecordOverride = record => consumed.Add(record.Id);
        try
        {
            var capture = BackupService.Capture(new[] { "transparency-off" }, null);
            Assert.True(File.Exists(capture));
            Assert.EndsWith(".json", capture);

            var before = Assert.Single(BackupService.ListBackupStatuses());
            Assert.True(before.Valid);
            Assert.True(before.PendingCount > 0);

            var result = BackupService.RestoreAll(new[] { "transparency-off" }, capture);
            Assert.Empty(result.Failures);

            Assert.False(File.Exists(capture), "全部记录消费后应改名为 .restored");
            var after = Assert.Single(BackupService.ListBackupStatuses());
            Assert.EndsWith(".json.restored", after.FileName);
            Assert.Equal(0, after.PendingCount);
            Assert.True(after.RestoredCount > 0);
        }
        finally
        {
            BackupService.RestoreRecordOverride = null;
        }
    }
}
