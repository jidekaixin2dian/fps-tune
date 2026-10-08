using System.Text.Json;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public class UpdateFlowTests
{
    [Theory]
    [InlineData("0.2.4", "0.2.4-beta", true)]
    [InlineData("0.2.4-beta.10", "0.2.4-beta.2", true)]
    [InlineData("0.2.4-beta.2", "0.2.4-beta.10", false)]
    [InlineData("0.2.4-beta", "0.2.4", false)]
    [InlineData("v0.2.4-beta+aaa", "0.2.4-beta+bbb", false)]
    public void Compares_release_progression_without_false_promotions(string latest, string current, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewer(latest, current));

    [Fact]
    public void Picks_highest_complete_release_not_first_created_release()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            Release("0.2.3"), Release("0.2.5", draft: true), Release("0.2.4-beta.2"),
            Release("0.2.6", complete: false), Release("0.2.4-beta.10")
        }));
        Assert.Equal("v0.2.4-beta.10", UpdateService.SelectRelease(doc.RootElement)?.ReleaseTag);
    }

    [Fact]
    public void Frozen_net8_line_is_not_an_update_for_the_current_product()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new[] { Release("1.6.2"), Release("0.2.4-beta") }));
        Assert.Equal("v0.2.4-beta", UpdateService.SelectRelease(doc.RootElement)?.ReleaseTag);
    }

    [Theory]
    [InlineData("0.2.4-")]
    [InlineData("0.2.4-beta..1")]
    [InlineData("0.2.4-beta.01")]
    [InlineData("0.2.4-beta/1")]
    [InlineData("0.2.4+hash+hash")]
    public void Rejects_malformed_semantic_versions(string value)
        => Assert.False(UpdateService.TryNormalizeVersion(value, out _));

    [Fact]
    public void Numeric_prerelease_comparison_does_not_overflow()
        => Assert.True(UpdateService.IsNewer("0.2.4-beta.1000000000000000000000", "0.2.4-beta.999999999999999999999"));

    [Theory]
    [InlineData("http://github.com/jidekaixin2dian/fps-tune/releases/download/v1/a", false)]
    [InlineData("https://github.com.evil.test/jidekaixin2dian/fps-tune/releases/a", false)]
    [InlineData("https://github.com/other/project/releases/a", false)]
    [InlineData("https://github.com/jidekaixin2dian/fps-tune/releases/download/v1/a", true)]
    public void Restricts_update_source(string url, bool expected)
        => Assert.Equal(expected, UpdateService.IsOfficialUrl(url));

    [Fact]
    public void Old_settings_default_to_startup_checks_without_losing_values()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"LowSpecMode\":true}")!;
        Assert.True(settings.CheckUpdatesOnStartup);
        Assert.True(settings.LowSpecMode);
    }

    private static object Release(string version, bool draft = false, bool complete = true)
    {
        var number = version.Split('-')[0];
        var baseUrl = "https://github.com/jidekaixin2dian/fps-tune/releases/";
        return new
        {
            tag_name = "v" + version, draft, html_url = baseUrl + "tag/v" + version,
            assets = complete ? new[]
            {
                new { name = "FpsTune-Setup-" + number + ".exe", browser_download_url = baseUrl + "download/v" + version + "/setup.exe" },
                new { name = "SHA256SUMS-v" + number + ".txt", browser_download_url = baseUrl + "download/v" + version + "/SHA256SUMS.txt" }
            } : Array.Empty<object>()
        };
    }
}
