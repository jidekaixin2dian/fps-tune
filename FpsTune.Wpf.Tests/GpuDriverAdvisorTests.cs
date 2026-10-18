using FpsTune.Wpf.Services;
using System.Text.Json;
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

    private static string Release(string whql = "1", string beta = "0", string host = "www.nvidia.com", string date = "Tue Oct 06, 2026")
        => JsonSerializer.Serialize(new { Success = "1", IDS = new[] { new { downloadInfo = new {
            Success = "1", Version = "617.42", Name = "GeForce%20Game%20Ready%20Driver", IsWHQL = whql, IsBeta = beta,
            DetailsURL = $"https://{host}/en-us/drivers/details/280104/", ReleaseDateTime = date } } } });

    [Theory]
    [InlineData(true, 26100, 135, 133, 1075)]
    [InlineData(false, 19045, 57, 131, 1076)]
    public async Task Exact_product_and_platform_select_the_official_release(bool notebook, int build, int os, int series, int product)
    {
        var name = "NVIDIA GeForce RTX 5070 Ti" + (notebook ? " Laptop GPU" : "");
        var requests = new List<string>();
        Task<string> Read(string url, CancellationToken _)
        {
            requests.Add(url);
            return Task.FromResult(url.Contains("TypeID=2")
                ? "<LookupValueSearch><LookupValue><Name>GeForce RTX 50 Series (Notebooks)</Name><Value>133</Value></LookupValue><LookupValue><Name>GeForce RTX 50 Series</Name><Value>131</Value></LookupValue></LookupValueSearch>"
                : url.Contains("TypeID=3")
                    ? $"<LookupValueSearch><LookupValue><Name>{name}</Name><Value>{product}</Value></LookupValue><LookupValue><Name>NVIDIA GeForce RTX 5070</Name><Value>999</Value></LookupValue></LookupValueSearch>"
                    : Release());
        }
        var advice = await GpuDriverAdvisor.QueryAsync(name + " · 12 GB (+1)", build, Read);
        Assert.Equal(name, advice.Product);
        Assert.Equal("617.42", advice.Version);
        Assert.Equal(new DateOnly(2026, 10, 6), advice.Released);
        Assert.Contains($"ParentID={series}", requests[1]);
        Assert.Contains($"psid={series}&pfid={product}&osID={os}", requests[2]);
        Assert.Contains("isWHQL=1&beta=0", requests[2]);
    }

    [Theory]
    [InlineData("0", "0", "www.nvidia.com", "Tue Oct 06, 2026")]
    [InlineData("1", "1", "www.nvidia.com", "Tue Oct 06, 2026")]
    [InlineData("1", "0", "example.com", "Tue Oct 06, 2026")]
    [InlineData("1", "0", "www.nvidia.com", "Wed Oct 06, 2027")]
    public void Rejects_unverified_beta_foreign_and_future_results(string whql, string beta, string host, string date)
        => Assert.Throws<InvalidOperationException>(() => GpuDriverAdvisor.ParseRelease(Release(whql, beta, host, date), "RTX 5070 Ti", "50", false,
            new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task Unknown_product_and_network_failure_do_not_fall_back_to_an_old_recommendation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GpuDriverAdvisor.QueryAsync("RTX 5080", 26100, (_, _) => Task.FromResult("<LookupValueSearch/>")));
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => GpuDriverAdvisor.QueryAsync("RTX 5080", 26100, (_, _) => throw new System.Net.Http.HttpRequestException()));
        var advice = GpuDriverAdvisor.ParseRelease(Release(), "RTX 5080", "50", false, new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        Assert.True(GpuDriverAdvisor.ShowHotfix(advice, new(2026, 10, 18)));
        Assert.False(GpuDriverAdvisor.ShowHotfix(advice, new(2026, 11, 18)));
        Assert.False(GpuDriverAdvisor.ShowHotfix(advice with { Version = "618.00" }, new(2026, 10, 18)));
        Assert.Contains("amd.com", GpuDriverAdvisor.OfficialUrl("AMD Radeon RX 7900 XTX"));
        Assert.Contains("intel.com", GpuDriverAdvisor.OfficialUrl("Intel Arc A770"));
    }
}
