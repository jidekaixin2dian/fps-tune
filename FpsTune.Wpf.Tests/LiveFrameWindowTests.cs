using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class LiveFrameWindowTests
{
    [Fact]
    public async Task Live_frames_share_the_recording_parser_and_remain_bounded_to_five_seconds()
    {
        var live = new LiveFrameWindow(() => 1000); live.SetEnabled(true);
        using var output = new MemoryStream();
        var csv = "Application,msBetweenPresents\n" + string.Concat(Enumerable.Repeat("game,8\n", 1000)) + "game,NaN\ngame,0\n";
        await SessionFpsRecorder.CaptureOutputAsync(new StringReader(csv), output, 65536, 2000,
            () => Assert.Fail("Unexpected output limit"), live.Add);
        var sample = live.Read()!;
        Assert.Equal(625, sample.Samples); Assert.Equal(5, sample.WindowSeconds);
        Assert.Equal(125, sample.Fps); Assert.Equal(125, sample.P1Low);
        Assert.Equal(1000, FrameTimeStats.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n')).Frame!.Samples);
    }
    [Fact]
    public void Disabled_insufficient_stale_and_restarted_streams_never_reuse_old_values()
    {
        long now = 1000; var live = new LiveFrameWindow(() => now);
        for (var i = 0; i < 100; i++) live.Add(10);
        Assert.Null(live.Read()); live.SetEnabled(true);
        for (var i = 0; i < 29; i++) live.Add(10);
        Assert.Null(live.Read()); live.Add(10); Assert.Equal(100, live.Read()!.Fps);
        now += 3001; Assert.Null(live.Read()); live.Add(20); Assert.Null(live.Read());
        for (var i = 0; i < 29; i++) live.Add(20);
        Assert.Equal(50, live.Read()!.Fps);
        live.SetEnabled(false); live.SetEnabled(true); Assert.Null(live.Read());
    }
    [Fact]
    public void Extremely_high_frame_rate_has_a_fixed_capacity_and_matches_recording_percentile_rule()
    {
        var live = new LiveFrameWindow(() => 0); live.SetEnabled(true);
        for (var i = 0; i < 100000; i++) live.Add(.001);
        Assert.Equal(32768, live.Read()!.Samples);
        live.SetEnabled(false); live.SetEnabled(true);
        for (var i = 0; i < 99; i++) live.Add(8);
        live.Add(60);
        Assert.Equal(1000d / 60, live.Read()!.P1Low);
    }
}
