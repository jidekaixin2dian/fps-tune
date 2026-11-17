using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>采样器参数族探测：内置 v2 走 <c>--timed</c>，FrameView 1.9 这类老版本只有 <c>--duration</c>。</summary>
public sealed class PresentMonSamplerTests
{
    [Fact]
    public void Family_is_v2_when_help_is_unavailable()
    {
        // 探测失败时不能误判成 v1：内置采样器就是 v2，先试现代参数才有意义。
        Assert.Equal(PresentMonSampler.ArgsFamily.V2, PresentMonSampler.FamilyFromHelp(null));
        Assert.Equal(PresentMonSampler.ArgsFamily.V2, PresentMonSampler.FamilyFromHelp(""));
    }

    [Fact]
    public void Family_is_v2_when_help_lists_timed()
    {
        const string help = "PresentMon 2.5.1\n  --timed seconds    Stop recording after the specified amount of time.\n  --process_name name";
        Assert.Equal(PresentMonSampler.ArgsFamily.V2, PresentMonSampler.FamilyFromHelp(help));
    }

    [Fact]
    public void Family_is_v1_when_help_only_has_duration()
    {
        const string help = "PresentMon 1.9.12728\n  -process_name name\n  -timed seconds\n  -output_file path";
        // 1.x 的帮助里出现的是单横线 -timed，不是 --timed。
        Assert.Equal(PresentMonSampler.ArgsFamily.V1, PresentMonSampler.FamilyFromHelp(help));
    }
}
