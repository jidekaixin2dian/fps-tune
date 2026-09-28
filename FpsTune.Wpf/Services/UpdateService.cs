using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

public sealed record UpdateInfo(
    string Version,
    string Url,
    string Notes,
    string? InstallerUrl = null,
    string? ChecksumUrl = null);

public static class UpdateService
{
    // 列表端点而非 /releases/latest：后者只返回「非 prerelease」的最新版，
    // 而本项目全部发布都标记为 pre-release，latest 恒 404——更新检查因此永久失效。
    // 列表按创建时间倒序，取第一个版本号可解析的条目（含 prerelease）。
    private const string ReleasesApi =
        "https://api.github.com/repos/jidekaixin2dian/fps-tune/releases?per_page=10";

    /// <summary>数字版本（三段），供机器协议、更新比较使用。</summary>
    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>人类可读版本（含预发布标识，如 0.1.0-beta），取 InformationalVersion 去掉构建哈希。</summary>
    public static string DisplayVersion
    {
        get
        {
            var informational = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational))
                return CurrentVersion;
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FpsTune/1.0");
            using var response = await client.GetAsync(ReleasesApi);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var tag = entry.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
                var notes = entry.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : "";
                var htmlUrl = entry.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;

                if (!TryNormalizeVersion(tag, out var version) || string.IsNullOrWhiteSpace(htmlUrl))
                    continue;

                return new UpdateInfo(
                    version,
                    htmlUrl,
                    notes ?? "",
                    FindAssetUrl(entry, InstallerAssetName(version)),
                    FindAssetUrl(entry, ChecksumAssetName(version)));
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewer(string latest, string current)
    {
        // 两侧都先归一化（剥离 v 前缀与 -beta 等预发布后缀），否则
        // "0.1.14-beta" 这类 tag 会让 Version.Parse 抛异常、比较恒为 false。
        if (!TryNormalizeVersion(latest, out var l) || !TryNormalizeVersion(current, out var c))
            return false;
        try
        {
            return Version.Parse(l) > Version.Parse(c);
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
            using var response = await client.GetAsync(ReleasesApi);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var tag = entry.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
                if (!TryNormalizeVersion(tag, out var version))
                    continue;
                return FindAssetUrl(entry, InstallerAssetName(version));
            }
        }
        catch
        {
        }
        return null;
    }

    internal static string InstallerAssetName(string version) => $"FpsTune-Setup-{version}.exe";

    internal static string ChecksumAssetName(string version) => $"SHA256SUMS-v{version}.txt";

    internal static bool TryNormalizeVersion(string? raw, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        // 预发布后缀（v0.1.13-beta）只用于标签，不参与数值比较
        var dash = text.IndexOf('-');
        if (dash > 0)
            text = text[..dash];

        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part =>
                part.Length == 0 || part.Any(ch => ch is < '0' or > '9') ||
                (part.Length > 1 && part[0] == '0')))
            return false;

        if (!Version.TryParse(text, out _))
            return false;

        version = text;
        return true;
    }

    internal static bool TryReadSha256(string manifest, string expectedFileName, out string hash)
    {
        hash = "";
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(expectedFileName))
            return false;

        var expectedName = FileNameOnly(expectedFileName);
        if (expectedName.Length == 0)
            return false;

        var found = false;
        foreach (var rawLine in manifest.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length <= 64 || !IsHex(line.AsSpan(0, 64)))
                continue;

            var fileName = line[64..].TrimStart();
            if (fileName.StartsWith('*'))
                fileName = fileName[1..];
            fileName = FileNameOnly(fileName.Trim());
            if (!string.Equals(fileName, expectedName, StringComparison.Ordinal))
                continue;

            if (found)
                return false;
            found = true;
            hash = line[..64];
        }

        return found;
    }

    internal static bool VerifySha256(string filePath, string expectedHash)
    {
        try
        {
            if (!File.Exists(filePath) || expectedHash.Length != 64 || !IsHex(expectedHash.AsSpan()))
                return false;

            var expected = Convert.FromHexString(expectedHash);
            using var stream = File.OpenRead(filePath);
            var actual = SHA256.HashData(stream);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindAssetUrl(JsonElement root, string expectedName)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (!string.Equals(name, expectedName, StringComparison.Ordinal) ||
                !asset.TryGetProperty("browser_download_url", out var urlEl))
                continue;

            var url = urlEl.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                return url.Trim();
        }

        return null;
    }

    private static string FileNameOnly(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                return false;
        }
        return true;
    }

    /// <summary>下载安装包到指定路径; progress 报告 0-100 百分比(服务器未给长度时不回调)。</summary>
    public static async Task DownloadAsync(string url, string targetFile, Action<double>? progress)
    {
        using var client = new HttpClient();
        // 默认 100 秒超时对 60MB 安装包的慢网下载不够；进度条已提供反馈，下载不设总时限
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
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
