using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>0.2.0 M1：实测判定沉淀存储——upsert 语义、规范化键、读写往返。</summary>
[Collection("BackupService serial")]
public sealed class VerdictStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "fpstune-verdict-tests-" + Guid.NewGuid().ToString("N"));

    public VerdictStoreTests()
    {
        Directory.CreateDirectory(_dir);
        VerdictStore.DirOverride = _dir;
    }

    public void Dispose()
    {
        VerdictStore.DirOverride = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static VerdictStore.VerdictEntry Make(string key, string kind, string[] items, bool keep = true) =>
        new(key, kind, items, "custom", "game.exe",
            AvgFpsBase: 100, AvgFpsTest: 103, P1LowBase: 55, P1LowTest: 58,
            StuttersBase: 3, StuttersTest: 2, Stable: true, Keep: keep,
            DeltaPct: 3.0, At: DateTime.Now, DurationSec: 90, Mode: "auto", RuleVersion: "v2-p99-3", ExperimentId: Guid.NewGuid().ToString("N"), P99Base: 20, P99Test: 19);

    [Fact]
    public void MakeKey_is_order_independent()
    {
        Assert.Equal(VerdictStore.MakeKey(new[] { "b", "a" }), VerdictStore.MakeKey(new[] { "a", "b" }));
        Assert.NotEqual(VerdictStore.MakeKey(new[] { "a", "b" }), VerdictStore.MakeKey(new[] { "a" }));
    }

    [Fact]
    public void Upsert_same_key_replaces_latest()
    {
        VerdictStore.Upsert(Make(VerdictStore.MakeKey(new[] { "a" }), "item", new[] { "a" }, keep: true));
        VerdictStore.Upsert(Make(VerdictStore.MakeKey(new[] { "a" }), "item", new[] { "a" }, keep: false));

        var all = VerdictStore.Load();
        var entry = Assert.Single(all);
        Assert.False(entry.Keep);
    }

    [Fact]
    public void Load_persists_across_calls_and_survives_missing_file()
    {
        Assert.Empty(VerdictStore.Load());

        VerdictStore.Upsert(Make(VerdictStore.MakeKey(new[] { "a", "b" }), "bundle", new[] { "a", "b" }));
        var loaded = VerdictStore.Load();
        var entry = Assert.Single(loaded);
        Assert.Equal("bundle", entry.Kind);
        Assert.Equal(new[] { "a", "b" }, entry.Items);
        Assert.Equal(103, entry.AvgFpsTest);
        Assert.Equal(3.0, entry.DeltaPct);
    }

    [Fact]
    public void FindItem_returns_only_item_kind_single_entry()
    {
        VerdictStore.Upsert(Make(VerdictStore.MakeKey(new[] { "a" }), "item", new[] { "a" }));
        VerdictStore.Upsert(Make(VerdictStore.MakeKey(new[] { "a", "b" }), "bundle", new[] { "a", "b" }));

        var hit = VerdictStore.FindItem("a");
        Assert.NotNull(hit);
        Assert.Equal("item", hit!.Kind);
        Assert.Null(VerdictStore.FindItem("c"));
    }

    [Fact]
    public void Load_returns_empty_on_corrupted_file()
    {
        File.WriteAllText(Path.Combine(_dir, "verdicts.json"), "{broken");
        Assert.Empty(VerdictStore.Load());
    }

    [Fact]
    public void Same_items_in_different_games_preserve_both_results()
    {
        var entry = Make("a", "item", ["a"]);
        VerdictStore.Upsert(entry with { Game = "one", GamePath = @"C:\one\game.exe" });
        VerdictStore.Upsert(entry with { Game = "two", GamePath = @"C:\two\game.exe" });
        Assert.Equal(2, VerdictStore.Load().Count);
        VerdictStore.Upsert(entry with { Game = "one", GamePath = @"c:\ONE\GAME.exe", Keep = false });
        var all = VerdictStore.Load();
        Assert.Equal(2, all.Count);
        Assert.False(all.Single(v => v.Game == "one").Keep);
        Assert.True(all.Single(v => v.Game == "two").Keep);
    }

    [Fact]
    public void Identical_exe_names_in_different_directories_do_not_share_badges()
    {
        var entry = Make("a", "item", ["a"]) with { Game = "game", GamePath = @"C:\one\game.exe" };
        VerdictStore.Upsert(entry);
        Assert.Null(VerdictStore.BadgeFor("a", gameName: "game", gamePath: @"C:\two\game.exe"));
        Assert.NotNull(VerdictStore.BadgeFor("a", gameName: "game", gamePath: @"c:\ONE\GAME.exe"));
    }

    [Fact]
    public void Legacy_records_without_paths_remain_readable_and_do_not_overwrite_new_games()
    {
        var entry = Make("a", "item", ["a"]) with { Game = "game", RuleVersion = null };
        VerdictStore.Upsert(entry);
        Assert.NotNull(VerdictStore.BadgeFor("a", gameName: "game", gamePath: @"C:\one\game.exe"));
        VerdictStore.Upsert(entry with { GamePath = @"C:\one\game.exe", DeltaPct = 9, RuleVersion = "v2-p99-3" });
        Assert.Equal(2, VerdictStore.Load().Count);
        Assert.Equal(Str.T("Str.VerdictBadge", 9.0),
            VerdictStore.BadgeFor("a", gameName: "game", gamePath: @"C:\one\game.exe"));
    }

    [Fact]
    public void Report_shows_rounds_interval_and_verdict_and_marks_untrusted_data_not_qualified()
    {
        var analyzed = Make("a", "item", ["a"]) with
        {
            RoundsBase = 3, RoundsTest = 3,
            AvgDiffLowerPct = 1.2, AvgDiffUpperPct = 4.8, HedgesG = 1.9,
            AnalysisVerdict = AbAnalysisCodes.VerdictConfirmed, AnalysisSufficient = true,
        };
        // 数据不可信（重复 / 离群 / 条件不一致）的实测不再算可信结论。
        var untrusted = Make("b", "item", ["b"]) with { AnalysisSufficient = false };
        // 旧记录没有分析字段（null）：保留原有资格口径，报告列留空不报错。
        var legacy = Make("c", "item", ["c"]);

        Assert.True(VerdictStore.IsQualified(analyzed));
        Assert.False(VerdictStore.IsQualified(untrusted));
        Assert.True(VerdictStore.IsQualified(legacy));

        var markdown = VerdictReport.Build([analyzed, untrusted, legacy]);
        Assert.Contains("3/3", markdown);
        Assert.Contains("+1.2 ~ +4.8%", markdown);
        Assert.Contains(AbEvidenceText.VerdictLabel(AbAnalysisCodes.VerdictConfirmed), markdown);
    }
}
