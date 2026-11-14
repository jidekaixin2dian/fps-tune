using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 收益档案（gain-archive.json）：读写往返、同键去重、容量截断、损坏留档、
/// 未来 schema 拒绝留档、导出/导入与预演计数、Summary 挑选。全部走临时根目录。
/// UserDataPaths.RootOverride 是全局静态：与其他设置它的测试类共享串行 collection。
/// </summary>
[Collection("BackupService serial")]
public sealed class GainArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fpstune-gain-tests-" + Guid.NewGuid().ToString("N"));

    public GainArchiveTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        GainArchiveService.FilePathOverride = null;
        GainArchiveService.CapacityOverride = 0;
        GainArchiveService.FingerprintOverride = () => "test-gpu|test-driver|test-cpu|16";
    }

    public void Dispose()
    {
        UserDataPaths.RootOverride = null;
        GainArchiveService.FilePathOverride = null;
        GainArchiveService.CapacityOverride = 0;
        GainArchiveService.FingerprintOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static GainArchiveEntry Entry(string gameId = "game.exe", string items = "sysmain", int offsetMinutes = 0,
        string verdict = "keep", double? diff = 5.0, string fingerprint = "test-gpu|test-driver|test-cpu|16") =>
        new(gameId, DateTimeOffset.Now.AddMinutes(offsetMinutes), [items], verdict,
            100, 100 + (diff ?? 0), diff, diff - 1, diff + 1, 2, -3,
            6, 6, 60, "backup.json", false, fingerprint);

    [Fact]
    public void Append_then_load_roundtrips_entry()
    {
        var entry = Entry(offsetMinutes: 0);
        GainArchiveService.Append(entry);

        var archive = GainArchiveService.Load();

        Assert.Single(archive.Entries);
        Assert.Equal(GainArchive.CurrentSchemaVersion, archive.SchemaVersion);
        var loaded = archive.Entries[0];
        Assert.Equal(entry.GameId, loaded.GameId);
        Assert.Equal(entry.At, loaded.At);
        Assert.Equal(["sysmain"], loaded.Items);
        Assert.Equal("keep", loaded.Verdict);
        Assert.Equal(5.0, loaded.AvgDiffPct);
        Assert.Equal(6, loaded.SegmentsBefore);
        Assert.Equal(60, loaded.SampleSeconds);
        Assert.Equal("test-gpu|test-driver|test-cpu|16", loaded.MachineFingerprint);
    }

    [Fact]
    public void Append_dedupes_identical_entries()
    {
        var at = DateTimeOffset.Now;
        GainArchiveService.Append(Entry() with { At = at });
        GainArchiveService.Append(Entry() with { At = at.AddSeconds(30) }); // 同一分钟同键

        Assert.Single(GainArchiveService.Load().Entries);
    }

    [Fact]
    public void Append_keeps_different_items_as_separate_entries()
    {
        GainArchiveService.Append(Entry(items: "sysmain"));
        var after1 = GainArchiveService.Load();
        GainArchiveService.Append(Entry(items: "mmcss-games"));
        var after2 = GainArchiveService.Load();

        var corrupt = Directory.GetFiles(_root, "*.corrupt", SearchOption.AllDirectories).Length;
        var file = File.Exists(GainArchiveService.FilePath) ? File.ReadAllText(GainArchiveService.FilePath) : "<missing>";
        Assert.True(after2.Entries.Count == 2,
            $"after1={after1.Entries.Count}, after2={after2.Entries.Count}, corrupt={corrupt}, file={file}");
    }

    [Fact]
    public void Append_truncates_oldest_beyond_capacity()
    {
        GainArchiveService.CapacityOverride = 3;
        for (var i = 0; i < 5; i++)
            GainArchiveService.Append(Entry(offsetMinutes: i, items: "item" + i));

        var archive = GainArchiveService.Load();
        Assert.Equal(3, archive.Entries.Count);
        Assert.DoesNotContain(archive.Entries, e => e.Items.Contains("item0"));
        Assert.Contains(archive.Entries, e => e.Items.Contains("item4"));
    }

    [Fact]
    public void Corrupt_file_is_archived_and_treated_as_empty()
    {
        File.WriteAllText(GainArchiveService.FilePath, "{broken");

        var archive = GainArchiveService.Load();

        Assert.Empty(archive.Entries);
        Assert.True(Directory.GetFiles(Path.GetDirectoryName(GainArchiveService.FilePath)!).Any(f => f.EndsWith(".corrupt")),
            "损坏档案应留档 .corrupt");
    }

    [Fact]
    public void Future_schema_is_rejected_and_archived_without_rewrite()
    {
        var path = GainArchiveService.FilePath;
        var json = """{"schemaVersion":99,"entries":[]}""";
        File.WriteAllText(path, json);

        var archive = GainArchiveService.Load();

        Assert.Empty(archive.Entries);
        var future = Directory.GetFiles(Path.GetDirectoryName(path)!).Where(f => f.Contains(".future-")).ToArray();
        Assert.Single(future);
        Assert.Equal(json, File.ReadAllText(future[0])); // 留档原样保留，不静默改写
    }

    [Fact]
    public void Export_import_roundtrip_preserves_entries()
    {
        GainArchiveService.Append(Entry(offsetMinutes: 0));
        GainArchiveService.Append(Entry(offsetMinutes: -10, items: "mmcss-games"));
        var exportPath = Path.Combine(_root, "export.json");
        Assert.Equal(2, GainArchiveService.Export(exportPath));

        // 清空后导入，数据一致（含指纹与时间）。
        File.Delete(GainArchiveService.FilePath);
        var preview = GainArchiveService.PreviewImport(exportPath);
        Assert.True(preview.Ok);
        Assert.Equal(2, preview.IncomingCount);
        Assert.Equal(2, preview.NewCount);
        Assert.Equal(0, preview.DuplicateCount);

        var imported = GainArchiveService.Import(exportPath);
        Assert.True(imported.Ok);
        Assert.Equal(2, imported.NewCount);

        var archive = GainArchiveService.Load();
        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.Items.Contains("sysmain") && e.MachineFingerprint == "test-gpu|test-driver|test-cpu|16");
    }

    [Fact]
    public void Import_preview_counts_duplicates_without_writing()
    {
        GainArchiveService.Append(Entry());
        var exportPath = Path.Combine(_root, "export2.json");
        GainArchiveService.Export(exportPath);
        var before = File.ReadAllText(GainArchiveService.FilePath);

        var preview = GainArchiveService.PreviewImport(exportPath);

        Assert.True(preview.Ok);
        Assert.Equal(1, preview.IncomingCount);
        Assert.Equal(0, preview.NewCount);
        Assert.Equal(1, preview.DuplicateCount);
        Assert.Equal(before, File.ReadAllText(GainArchiveService.FilePath)); // 预演不得写文件
    }

    [Fact]
    public void Import_skips_duplicates_and_adds_only_new()
    {
        GainArchiveService.Append(Entry(offsetMinutes: 0, items: "sysmain"));
        var export1 = Path.Combine(_root, "export3a.json");
        GainArchiveService.Export(export1); // 只含第一条

        GainArchiveService.Append(Entry(offsetMinutes: -10, items: "wer-off", fingerprint: "other-machine"));
        var export2 = Path.Combine(_root, "export3b.json");
        GainArchiveService.Export(export2); // 含两条（旧条 + 另一台机器的新条）

        // 清空到只有第一条的状态，再导入 export2：应跳过重复、只加另一台机器的那条。
        GainArchiveService.FilePathOverride = Path.Combine(_root, "reset.json");
        File.Delete(GainArchiveService.FilePathOverride);
        GainArchiveService.Append(Entry(offsetMinutes: 0, items: "sysmain"));

        var result = GainArchiveService.Import(export2);

        Assert.True(result.Ok);
        Assert.Equal(1, result.NewCount);
        Assert.Equal(1, result.DuplicateCount);
        var archive = GainArchiveService.Load();
        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.MachineFingerprint == "other-machine");
        GainArchiveService.FilePathOverride = null;
    }

    [Fact]
    public void Import_rejects_unparseable_file()
    {
        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "not json at all");

        Assert.False(GainArchiveService.PreviewImport(bad).Ok);
        Assert.False(GainArchiveService.Import(bad).Ok);
    }

    [Fact]
    public void Summary_picks_best_keep_and_latest()
    {
        var archive = new GainArchive(GainArchive.CurrentSchemaVersion,
        [
            Entry(offsetMinutes: -30, items: "a", verdict: "keep", diff: 2),
            Entry(offsetMinutes: -20, items: "b", verdict: "rollback", diff: 8), // 回滚的不算最佳
            Entry(offsetMinutes: -10, items: "c", verdict: "keep", diff: 6),
            Entry(offsetMinutes: -5, items: "d", verdict: "insufficient", diff: null),
        ]);

        var (best, latest, count) = GainArchiveService.Summary(archive, "game.exe");

        Assert.Equal(4, count);
        Assert.NotNull(best);
        Assert.Equal(["c"], best!.Items);
        Assert.Equal(6, best.AvgDiffPct);
        Assert.NotNull(latest);
        Assert.Equal(["d"], latest!.Items);
    }

    [Fact]
    public void Summary_isolates_games()
    {
        var archive = new GainArchive(GainArchive.CurrentSchemaVersion,
            [Entry(gameId: "other.exe", diff: 9), Entry(gameId: "game.exe", diff: 3)]);

        var (best, _, count) = GainArchiveService.Summary(archive, "game.exe");

        Assert.Equal(1, count);
        Assert.Equal(3, best!.AvgDiffPct);
    }
}
