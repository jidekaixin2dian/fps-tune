using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 驱动版本建议的解析与查表守卫。
///
/// 重点在**不要猜**：识别不出系列时返回 null，而不是硬套一个默认系列——
/// 给错驱动版本比不给更糟。
/// </summary>
public class GpuDriverAdvisorTests
{
    [Theory]
    // 真实会遇到的显卡名形态（含 Ti / SUPER / Laptop 后缀）
    [InlineData("NVIDIA GeForce RTX 5070 Ti Laptop GPU", "50")]
    [InlineData("NVIDIA GeForce RTX 5080", "50")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", "40")]
    [InlineData("NVIDIA GeForce RTX 3060", "30")]
    [InlineData("NVIDIA GeForce RTX 2060", "20")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", "16")]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", "10")]
    [InlineData("rtx 3080", "30")]                 // 大小写不敏感
    public void Parses_nvidia_series_from_gpu_name(string gpu, string expected)
        => Assert.Equal(expected, GpuDriverAdvisor.NvidiaSeries(gpu));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("Intel Arc A770")]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("NVIDIA GeForce RTX 9070")]        // 未收录的代号 -> 不猜
    [InlineData("NVIDIA GeForce GTX 980")]         // 两位尾数不构成四位型号
    public void Returns_null_when_series_cannot_be_determined(string? gpu)
        => Assert.Null(GpuDriverAdvisor.NvidiaSeries(gpu));

    [Fact]
    public void Non_nvidia_gpu_gets_no_advice()
    {
        Assert.Null(GpuDriverAdvisor.For("AMD Radeon RX 7900 XTX"));
        Assert.Null(GpuDriverAdvisor.For(null));
    }

    [Fact]
    public void Each_supported_series_has_a_nonempty_note()
    {
        foreach (var series in new[] { "10", "16", "20", "30", "40", "50" })
        {
            var advice = GpuDriverAdvisor.For($"NVIDIA GeForce RTX {series}70");
            Assert.NotNull(advice);
            Assert.Equal(series, advice!.Series);
            Assert.False(string.IsNullOrWhiteSpace(advice.Note));
        }
    }

    [Fact]
    public void Fifty_series_is_honest_about_having_no_agreed_stable_version()
    {
        // 社区对 50 系没有公认稳定版——这里必须如实留空，不能编一个版本号出来
        var advice = GpuDriverAdvisor.For("NVIDIA GeForce RTX 5080");
        Assert.NotNull(advice);
        Assert.Equal("", advice!.Stable);
    }

    [Fact]
    public void Advice_carries_the_source_note()
        => Assert.Contains("2026-01-23", GpuDriverAdvisor.SourceNote);
}
