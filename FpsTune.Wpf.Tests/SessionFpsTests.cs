using System.Diagnostics;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>0.2.0 C-E：帧时间解析（实验/会话共用口径）与会话 FPS 采集器的降级语义。</summary>
public sealed class SessionFpsTests
{
    [Fact]
    public async Task Compact_capture_bounds_bytes_frames_and_unterminated_lines_without_silently_claiming_completion()
    {
        foreach (var (text, bytes, lines) in new[] { ("FrameTime\n" + string.Concat(Enumerable.Repeat("8\n", 100)), 128L, 100), ("FrameTime\n" + string.Concat(Enumerable.Repeat("8\n", 100)), 4096L, 50), ("FrameTime\n" + new string('a', 20000), 65536L, 100) })
        {
            using var output = new MemoryStream(); var limited = false;
            await SessionFpsRecorder.CaptureOutputAsync(new StringReader(text), output, bytes, lines, () => limited = true);
            Assert.True(limited); Assert.True(output.Length <= bytes);
        }
        using var normal = new MemoryStream();
        await SessionFpsRecorder.CaptureOutputAsync(new StringReader("FrameTime\n8\n"), normal, 1024, 100, () => Assert.Fail("Normal capture was truncated"));
        Assert.Equal("FrameTime\n8\n", System.Text.Encoding.UTF8.GetString(normal.ToArray()));
    }

    [Theory]
    [InlineData("FrameTime", "8.123456789012345")]
    [InlineData("FPS", "123.456789012345")]
    public async Task Compact_capture_preserves_exact_statistics_from_wide_quoted_csv_and_counts_only_valid_frames(string column, string value)
    {
        var csv = new[] { $"\uFEFF\"Application\",\"extra,label\",\"{column}\",Unused" }
            .Concat(Enumerable.Repeat($"\"game,edition\",\"{new string('x', 800)}\",\"{value}\",ignored", 39))
            .Concat(["\"game,edition\",unused,60,ignored", "game,unused,NaN", "game,unused,0", "game,unused,Infinity", "short"]);
        var text = string.Join("\r\n", csv); // No final newline; both readers must include the last valid record.
        using var compact = new MemoryStream();
        await SessionFpsRecorder.CaptureOutputAsync(new StringReader(text), compact, 4096, 41, () => Assert.Fail("Unrelated columns consumed frame storage"));
        var saved = System.Text.Encoding.UTF8.GetString(compact.ToArray());
        Assert.Equal(FrameTimeStats.Parse(csv).Frame, FrameTimeStats.Parse(saved.Split('\n')).Frame);
        Assert.Equal(40, FrameTimeStats.Parse(saved.Split('\n')).Frame!.Samples);
        Assert.True(compact.Length < 1024); Assert.True(text.Length > 30000);
        Assert.DoesNotContain("game", saved); Assert.DoesNotContain("ignored", saved);
    }

    [Fact]
    public async Task Compact_capture_rejects_unknown_columns_and_accepts_exact_limit_and_final_record()
    {
        using var invalid = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionFpsRecorder.CaptureOutputAsync(new StringReader("a,b\n1,2\n"), invalid, 1024, 100, () => Assert.Fail("Not a capacity error")));
        using var compact = new MemoryStream();
        await SessionFpsRecorder.CaptureOutputAsync(new StringReader("FrameTime\r\n8"), compact, 12, 2, () => Assert.Fail("Exact limit must fit"));
        Assert.Equal("FrameTime\n8\n", System.Text.Encoding.UTF8.GetString(compact.ToArray()));
    }

    private static string[] MakeCsv(int frameCount, double ms = 7.0)
    {
        var lines = new List<string> { "Application,msBetweenPresents" };
        for (var i = 0; i < frameCount; i++)
            lines.Add($"game,{ms + (i % 3) * 0.1}");
        return lines.ToArray();
    }

    [Fact]
    public void Parse_computes_avg_p1low_p99_and_stutters()
    {
        // 40 帧：39 帧 7ms（~142.9 FPS）+ 1 帧 60ms（>50ms 计一次卡顿）
        var lines = new List<string> { "Application,msBetweenPresents" };
        for (var i = 0; i < 39; i++)
            lines.Add("game,7");
        lines.Add("game,60");

        var parsed = FrameTimeStats.Parse(lines);

        Assert.Null(parsed.Error);
        var frame = Assert.IsType<FrameSummary>(parsed.Frame);
        Assert.Equal(40, frame.Samples);
        Assert.Equal(1, frame.Stutters);
        Assert.Equal(60, frame.P99Ms);
        Assert.True(frame.AvgFps > 115 && frame.AvgFps < 125, $"avgFps={frame.AvgFps}");
        Assert.Equal(Math.Round(1000.0 / 60.0, 2), frame.P1Low);
    }

    [Fact]
    public void Parse_reports_structured_errors()
    {
        var unknown = FrameTimeStats.Parse(new[] { "a,b", "1,2" });
        Assert.Equal(FrameParseError.UnknownColumns, unknown.Error);
        Assert.Equal(new[] { "a", "b" }, unknown.Header);

        var thin = FrameTimeStats.Parse(MakeCsv(10));
        Assert.Equal(FrameParseError.TooFewFrames, thin.Error);
        Assert.Equal(10, thin.FrameCount);

        var empty = FrameTimeStats.Parse(new[] { "a,b" });
        Assert.Equal(FrameParseError.TooFewLines, empty.Error);
    }

    [Fact]
    public void Parse_accepts_v1_and_v2_column_names_and_filters_nonpositive()
    {
        foreach (var header in new[] { "msBetweenPresents", "frame_time" })
        {
            var lines = new List<string> { $"t,{header}" };
            for (var i = 0; i < 30; i++)
                lines.Add(i % 2 == 0 ? "0" : "8"); // 0 被过滤，只收 15 个正数 → 不足 30
            Assert.Equal(FrameParseError.TooFewFrames, FrameTimeStats.Parse(lines).Error);
        }

        var ok = new List<string> { "frame_time" };
        for (var i = 0; i < 31; i++)
            ok.Add("8");
        Assert.NotNull(FrameTimeStats.Parse(ok).Frame);
    }

    [Fact]
    public void Recorder_degrades_with_reason_keys_when_presentmon_or_game_missing()
    {
        var noPm = new SessionFpsRecorder(
            findPresentMon: () => null,
            findGameProcess: _ => new FakeProcess());
        Assert.False(noPm.Start(@"C:\game.exe"));
        Assert.Equal("Str.FpsReasonNoPresentMon", noPm.UnavailableReasonKey);
        Assert.Null(noPm.Stop());

        var noGame = new SessionFpsRecorder(
            findPresentMon: () => @"C:\pm.exe",
            findGameProcess: _ => null);
        Assert.False(noGame.Start(@"C:\game.exe"));
        Assert.Equal("Str.FpsReasonNoGameProcess", noGame.UnavailableReasonKey);
        Assert.Null(noGame.Stop());
    }

    [Fact]
    public void Recorder_start_without_game_path_reports_missing_process()
    {
        var rec = new SessionFpsRecorder(
            findPresentMon: () => @"C:\pm.exe",
            findGameProcess: _ => null);
        Assert.False(rec.Start(gamePath: null));
        Assert.Equal("Str.FpsReasonNoGameProcess", rec.UnavailableReasonKey);
    }

    [Fact]
    public void Recorder_parses_stdout_frames_on_stop()
    {
        // 成功路径用注入的假进程验证解析链路（真实 PresentMon 链路随候选部署目检）。
        var proc = new FakeProcess();
        var rec = new SessionFpsRecorder(
            findPresentMon: () => @"C:\pm.exe",
            findGameProcess: _ => proc,
            startProcess: _ => proc,
            readOutput: _ => Task.FromResult(MakeCsv(40)),
            kill: _ => { });
        Assert.True(rec.Start(@"C:\game.exe"));

        var stats = rec.Stop();

        Assert.NotNull(stats);
        Assert.Equal(40, stats!.Samples);
        Assert.Null(rec.UnavailableReasonKey);
    }

    [Fact]
    public void Recorder_reports_parse_failure_with_ascii_detail()
    {
        var proc = new FakeProcess();
        var rec = new SessionFpsRecorder(
            findPresentMon: () => @"C:\pm.exe",
            findGameProcess: _ => proc,
            startProcess: _ => proc,
            readOutput: _ => Task.FromResult(MakeCsv(5)),
            kill: _ => { });
        Assert.True(rec.Start(@"C:\game.exe"));

        Assert.Null(rec.Stop());
        Assert.Equal("Str.FpsReasonParseFailed", rec.UnavailableReasonKey);
        Assert.Equal("frames=5", rec.ParseError);
    }

    [Theory]
    [InlineData(50, 60, 95, 97, "gpu")]      // GPU 95 位 97 且平均 95 → GPU 受限
    [InlineData(70, 95, 45, 50, "cpu")]      // GPU 平均 45 有余量 + CPU 95 位 95 → CPU 受限
    [InlineData(50, 70, 70, 80, "balanced")] // 双侧均未达阈值
    [InlineData(65, 92, 85, 92, "balanced")] // GPU 95 位 92 未到 95 线、CPU 侧 GPU 有余量不成立 → 未判定
    public void ClassifyBottleneck_applies_documented_thresholds(
        double cpuAvg, double cpuP95, double gpuAvg, double gpuP95, string expected)
    {
        var summary = new SessionSummary
        {
            SampleCount = 100,
            Duration = TimeSpan.FromMinutes(5),
            Cpu = new MetricStats(100, cpuAvg, 100, cpuAvg - 10, cpuP95),
            Gpu = new MetricStats(100, gpuAvg, 100, gpuAvg - 10, gpuP95),
        };
        Assert.Equal(expected, SessionInsights.ClassifyBottleneck(summary)!.Kind);
    }

    [Fact]
    public void ClassifyBottleneck_returns_null_when_signal_missing()
    {
        var noGpu = new SessionSummary { SampleCount = 10, Duration = TimeSpan.FromMinutes(1), Cpu = new MetricStats(10, 50, 60, 40, 55) };
        Assert.Null(SessionInsights.ClassifyBottleneck(noGpu));
        var noCpu = new SessionSummary { SampleCount = 10, Duration = TimeSpan.FromMinutes(1), Gpu = new MetricStats(10, 50, 60, 40, 55) };
        Assert.Null(SessionInsights.ClassifyBottleneck(noCpu));
    }

    private sealed class FakeProcess : Process
    {
    }

    [Fact]
    public void Parse_fps_column_is_converted_to_frame_time_and_nonfinite_values_are_ignored()
    {
        var lines = new[] { "FPS" }.Concat(Enumerable.Repeat("125", 30)).Concat(["NaN", "Infinity", "0"]);
        var parsed = FrameTimeStats.Parse(lines);
        Assert.Equal(30, parsed.Frame!.Samples);
        Assert.Equal(125, parsed.Frame.AvgFps);
        Assert.Equal(8, parsed.Frame.P99Ms);
    }

    [Fact]
    public void Parse_reads_stream_once_without_materializing_csv_lines()
    {
        IEnumerable<string> Rows()
        {
            yield return "FrameTime";
            for (var i = 0; i < 100000; i++) yield return "8";
        }
        var parsed = FrameTimeStats.Parse(Rows());
        Assert.Equal(100000, parsed.Frame!.Samples);
        Assert.Equal(125, parsed.Frame.AvgFps);
    }
}
