using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>直播软件识别专项：并入后台应用整理的进程名匹配（OBS/Streamlabs/XSplit/vMix/虎牙直播伴侣）。</summary>
public sealed class StreamingAppDetectorTests
{
    [Theory]
    [InlineData(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", true)]
    [InlineData(@"D:\Broadcast\obs32.exe", true)]
    [InlineData(@"C:\Streamlabs\Streamlabs Desktop\app\streamlabs.exe", true)]
    [InlineData(@"C:\Program Files (x86)\XSplit Broadcaster\XSplit.Core.exe", true)]
    [InlineData(@"C:\Program Files\VMix\vmix64.exe", true)]
    [InlineData(@"E:\Live\LiveHime.exe", true)]
    [InlineData(@"E:\Live\livehime.EXE", true)]
    public void Detects_known_streaming_processes(string path, bool expected)
        => Assert.Equal(expected, StreamingAppDetector.IsStreamingProcess(path));

    [Theory]
    [InlineData(@"C:\Games\Cyberpunk2077.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Tools\observer.exe")]
    [InlineData(@"C:\Tools\vmixbook.exe")]
    [InlineData(@"")]
    public void Ignores_non_streaming_processes(string path)
        => Assert.False(StreamingAppDetector.IsStreamingProcess(path));
}
