using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>HUD 帧时间折线的数据缓冲与判定：窗口滚动、中位数、卡顿阈值、1% Low 口径。</summary>
public sealed class HudFrameTimelineTests
{
    private const double SixtyFps = 1000.0 / 60;

    [Fact]
    public void Analyze_returns_null_below_minimum_samples()
    {
        var timeline = new HudFrameTimeline();
        for (var i = 0; i < 29; i++) timeline.Append(SixtyFps);

        Assert.Null(timeline.Analyze());

        timeline.Append(SixtyFps);
        Assert.NotNull(timeline.Analyze());
    }

    [Fact]
    public void Append_rolls_window_beyond_sixty_frames()
    {
        var timeline = new HudFrameTimeline();
        // 先铺 60 帧慢帧（16.7ms），再灌 60 帧快帧（8.3ms）：窗口应只剩快帧。
        for (var i = 0; i < 60; i++) timeline.Append(SixtyFps);
        for (var i = 0; i < 60; i++) timeline.Append(SixtyFps / 2);

        var snapshot = timeline.Analyze()!;

        Assert.Equal(60, snapshot.Frames.Count);
        Assert.All(snapshot.Frames, f => Assert.Equal(SixtyFps / 2, f, 3));
    }

    [Fact]
    public void Append_ignores_non_positive_and_non_finite()
    {
        var timeline = new HudFrameTimeline();
        timeline.Append(0);
        timeline.Append(-5);
        timeline.Append(double.NaN);
        timeline.Append(SixtyFps);

        Assert.Null(timeline.Analyze()); // 只有 1 帧有效，不足 30
    }

    [Fact]
    public void Clear_resets_window()
    {
        var timeline = new HudFrameTimeline();
        for (var i = 0; i < 60; i++) timeline.Append(SixtyFps);
        timeline.Clear();

        Assert.Null(timeline.Analyze());
    }

    [Fact]
    public void Median_and_stutter_detection_match_threshold()
    {
        var timeline = new HudFrameTimeline();
        for (var i = 0; i < 59; i++) timeline.Append(SixtyFps);
        timeline.Append(SixtyFps * 4); // 一帧 66.7ms 卡顿（> 2× 中位数 16.7ms）

        var snapshot = timeline.Analyze()!;

        Assert.Equal(SixtyFps, snapshot.MedianMs, 2);
        var stutter = Assert.Single(snapshot.StutterIndices);
        Assert.Equal(59, stutter); // 卡顿帧在窗口末尾
        Assert.Equal(4.0, snapshot.Frames[59] / SixtyFps, 1);
    }

    [Fact]
    public void Borderline_double_median_is_marked_stutter()
    {
        var timeline = new HudFrameTimeline();
        for (var i = 0; i < 59; i++) timeline.Append(SixtyFps);
        timeline.Append(SixtyFps * 2.01); // 恰好越过 2× 阈值

        Assert.Single(timeline.Analyze()!.StutterIndices);
    }

    [Fact]
    public void Exactly_double_median_is_not_stutter()
    {
        var timeline = new HudFrameTimeline();
        for (var i = 0; i < 59; i++) timeline.Append(SixtyFps);
        timeline.Append(SixtyFps * 2); // 等于 2× 阈值不算卡顿

        Assert.Empty(timeline.Analyze()!.StutterIndices);
    }

    [Fact]
    public void P1Low_uses_frame_time_stats_percentile()
    {
        var timeline = new HudFrameTimeline();
        for (var i = 0; i < 59; i++) timeline.Append(SixtyFps);
        timeline.Append(SixtyFps * 4); // p99 帧时间 = 66.7ms（nearest-rank）

        var snapshot = timeline.Analyze()!;

        Assert.Equal(1000 / (SixtyFps * 4), snapshot.P1Low, 1);
    }
}

/// <summary>卡顿取证快照：采集落盘与 HAGS/MPO 注册表读取。</summary>
[Collection("BackupService serial")]
public sealed class StutterEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fpstune-stutter-tests-" + Guid.NewGuid().ToString("N"));

    public StutterEvidenceTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        GainArchiveService.FingerprintOverride = null;
        StutterEvidence.GpuReadOverride = () => new GpuTelemetrySnapshot(
            new GpuReading("Test GPU", 88, 4096, 8192, 65, 180, 240, 1800, 5000, 60, 0),
            GpuVerdict.GpuBusy, DateTimeOffset.Now, null);
        StutterEvidence.BackgroundTopOverride = () => ["chrome.exe 12%", "steam.exe 5%", "discord.exe 3%"];
    }

    public void Dispose()
    {
        UserDataPaths.RootOverride = null;
        StutterEvidence.GpuReadOverride = null;
        StutterEvidence.BackgroundTopOverride = null;
        StutterEvidence.EvidenceRootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static StutterEvidenceContext Context() => new(
        "UiTestGame", [66.7, 66.7], SixtyFpsOf(16.7), 60, 59.5);

    private static double SixtyFpsOf(double ms) => ms;

    [Fact]
    public async Task Capture_writes_snapshot_with_readings_and_states()
    {
        var path = await StutterEvidence.CaptureAsync(Context());

        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(_root, "stutter"), path);
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        Assert.Contains("\"gameName\": \"UiTestGame\"", json);
        Assert.Contains("Test GPU", json);
        Assert.Contains("chrome.exe", json);
        Assert.Contains("66.7", json);
        // HAGS/MPO 读不到时如实为 null（测试环境无对应键值时两者都是注册表实际值）。
        Assert.True(json.Contains("\"hagsState\"") && json.Contains("\"mpoState\""));
    }

    [Fact]
    public async Task Capture_repeated_writes_distinct_files()
    {
        var first = await StutterEvidence.CaptureAsync(Context());
        var second = await StutterEvidence.CaptureAsync(Context());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task History_reads_old_and_new_snapshots_without_touching_unreadable_files()
    {
        Assert.False(StutterEvidence.ReadHistory().Unavailable);
        Assert.Empty(StutterEvidence.ReadHistory().Records);
        var current = await StutterEvidence.CaptureAsync(Context());
        Assert.NotNull(current);
        var legacy = Path.Combine(StutterEvidence.EvidenceRoot, "stutter-20260101-010203.json");
        var legacyJson = """
            {"gameName":"Earlier game","at":"2026-01-01T01:02:03+08:00","stutterFramesMs":[],"medianMs":0,"fps":0,"p1Low":0,"backgroundTop":[]}
            """;
        File.WriteAllText(legacy, legacyJson);
        var broken = Path.Combine(StutterEvidence.EvidenceRoot, "stutter-broken.json");
        File.WriteAllText(broken, "{");
        File.WriteAllText(Path.Combine(StutterEvidence.EvidenceRoot, "unmanaged.json"), legacyJson);

        var history = StutterEvidence.ReadHistory();

        Assert.False(history.Unavailable);
        Assert.Equal(1, history.Skipped);
        Assert.Equal(new[] { "UiTestGame", "Earlier game" }, history.Records.Select(record => record.GameName));
        Assert.Equal(legacyJson, File.ReadAllText(legacy));
        Assert.Equal("{", File.ReadAllText(broken));
    }

    [Fact]
    public async Task History_uses_capture_time_across_older_files_and_keeps_the_limit()
    {
        var current = await StutterEvidence.CaptureAsync(Context());
        Assert.NotNull(current);
        File.SetLastWriteTimeUtc(current, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < 130; i++)
        {
            var at = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i);
            File.WriteAllText(Path.Combine(StutterEvidence.EvidenceRoot, $"stutter-{i}.json"),
                $$"""{"gameName":"past-{{i}}","at":"{{at:O}}","stutterFramesMs":[],"backgroundTop":[]}""");
        }

        var records = StutterEvidence.ReadHistory().Records;
        Assert.Equal(StutterEvidence.HistoryLimit, records.Count);
        Assert.Equal("UiTestGame", records[0].GameName);
        Assert.Equal("past-129", records[1].GameName);
    }

    [Fact]
    public async Task Capture_failure_returns_null_without_throwing()
    {
        StutterEvidence.EvidenceRootOverride = Path.Combine(_root, "blocked-file");
        File.WriteAllText(Path.Combine(_root, "blocked-file"), "not a directory");

        var path = await StutterEvidence.CaptureAsync(Context());

        Assert.True(StutterEvidence.ReadHistory().Unavailable);
        Assert.Null(path); // 目录被文件占位：写入失败如实返回 null，不抛
    }

    [Fact]
    public void Hags_and_mpo_readers_do_not_throw_offline()
    {
        // 读不到键/值时返回 null（测试环境值不定，但绝不抛）。
        var hags = StutterEvidence.ReadHags();
        var mpo = StutterEvidence.ReadMpo();
        Assert.True(hags is null or "on" or "off");
        Assert.True(mpo is null or "off");
    }
}
