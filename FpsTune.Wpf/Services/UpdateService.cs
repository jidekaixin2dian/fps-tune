using System.IO;
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

    /// <summary>在最新 Release 的资产里找安装包(FpsTune-Setup-x.y.z.exe)的下载地址。</summary>
    public static async Task<string?> FindInstallerUrlAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FpsTune/1.0");
            using var response = await client.GetAsync(ReleaseApi);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (!doc.RootElement.TryGetProperty("assets", out var assets))
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                if (name.StartsWith("FpsTune-Setup-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var urlEl))
                {
                    return urlEl.GetString();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>下载安装包到指定路径; progress 报告 0-100 百分比(服务器未给长度时不回调)。</summary>
    public static async Task DownloadAsync(string url, string targetFile, Action<double>? progress)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FpsTune/1.0");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(targetFile, FileMode.Create);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            written += read;
            if (total > 0)
                progress?.Invoke(written * 100.0 / total);
        }
    }
}
