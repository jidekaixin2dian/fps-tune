using System.Net.Http;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

public sealed record UpdateInfo(string Version, string Url, string Notes);

public static class UpdateService
{
    private const string ReleaseApi =
        "https://api.github.com/repos/jiaxindeyang-a11y/fps-tune/releases/latest";

    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FpsTune/1.0");
            using var response = await client.GetAsync(ReleaseApi);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
                return null;

            return new UpdateInfo(tag.TrimStart('v'), htmlUrl, notes ?? "");
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewer(string latest, string current)
    {
        try
        {
            var l = Version.Parse(latest);
            var c = Version.Parse(current);
            return l > c;
        }
        catch
        {
            return false;
        }
    }
}
