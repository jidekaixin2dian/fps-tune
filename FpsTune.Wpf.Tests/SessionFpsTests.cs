using System.Diagnostics;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>0.2.0 C-E：帧时间解析（实验/会话共用口径）与会话 FPS 采集器的降级语义。</summary>
public sealed class SessionFpsTests
{
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

    private sealed class FakeProcess : Process
    {
    }
}
