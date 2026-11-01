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
    string? ChecksumUrl = null,
    string? ReleaseTag = null);

public static class UpdateService
{
    // 列表端点同时覆盖正式与测试版本；不依赖 GitHub Latest 标记或创建顺序。
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

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FpsTune/1.0");
            using var response = await client.GetAsync(ReleasesApi, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return SelectRelease(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    internal static UpdateInfo? SelectRelease(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        UpdateInfo? best = null;
        foreach (var entry in releases.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                (entry.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)) continue;
            var tag = entry.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var page = entry.TryGetProperty("html_url", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (!TryNormalizeVersion(tag, out var version) || !IsOfficialUrl(page)) continue;
            // 1.x is the frozen .NET 8 product line, not an upgrade from the .NET 10 Beta line.
            // Those version numbers remain reserved; a future maintained major must use a new number.
            if (Version.Parse(version).Major == 1) continue;
            var installer = FindAssetUrl(entry, InstallerAssetName(version));
            var checksum = FindAssetUrl(entry, ChecksumAssetName(version));
            if (!IsOfficialUrl(installer) || !IsOfficialUrl(checksum)) continue;
            var notes = entry.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "";
            var candidate = new UpdateInfo(version, page!, notes ?? "", installer, checksum, tag);
            if (best is null || IsNewer(tag!, best.ReleaseTag ?? best.Version)) best = candidate;
        }
        return best;
    }

    internal static async Task<UpdateInfo?> GetReleaseAsync(string version, CancellationToken token)
    {
        if (!TryNormalizeVersion(version, out _)) return null;
        var tag = version.StartsWith('v') ? version : "v" + version;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FpsTune/1.0");
        using var response = await client.GetAsync("https://api.github.com/repos/jidekaixin2dian/fps-tune/releases/tags/" + Uri.EscapeDataString(tag), token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(token);
        using var doc = JsonDocument.Parse("[" + json + "]");
        var release = SelectRelease(doc.RootElement);
        return release?.ReleaseTag == tag ? release : null;
    }

    internal static bool IsOfficialUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/jidekaixin2dian/fps-tune/releases/", StringComparison.Ordinal);

    public static bool IsNewer(string latest, string current)
    {
        // 先比较数字版本，再按预发布标识比较；构建 SHA 不影响更新顺序。
        if (!TryNormalizeVersion(latest, out var l) || !TryNormalizeVersion(current, out var c))
            return false;
        try
        {
            var comparison = Version.Parse(l).CompareTo(Version.Parse(c));
            if (comparison != 0) return comparison > 0;
            var lp = Prerelease(latest);
            var cp = Prerelease(current);
            if (lp == cp) return false;
            if (lp.Length == 0) return true;
            if (cp.Length == 0) return false;
            var left = lp.Split('.');
            var right = cp.Split('.');
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                if (left[i] == right[i]) continue;
                var ln = left[i].All(char.IsAsciiDigit);
                var rn = right[i].All(char.IsAsciiDigit);
                return ln && rn ? (left[i].Length != right[i].Length ? left[i].Length > right[i].Length
                    : string.CompareOrdinal(left[i], right[i]) > 0)
                    : ln != rn ? !ln : string.CompareOrdinal(left[i], right[i]) > 0;
            }
            return left.Length > right.Length;
        }
        catch
        {
            return false;
        }
    }

    private static string Prerelease(string value)
    {
        var text = value.Trim().Split('+')[0];
        var dash = text.IndexOf('-');
        return dash < 0 ? "" : text[(dash + 1)..];
    }

    /// <summary>在最新 Release 的资产里找安装包(FpsTune-Setup-x.y.z.exe)的下载地址。</summary>
    public static async Task<string?> FindInstallerUrlAsync()
    {
        return (await CheckAsync())?.InstallerUrl;
    }

    internal static string InstallerAssetName(string version) => $"FpsTune-Setup-{version}.exe";

    internal static string ChecksumAssetName(string version) => $"SHA256SUMS-v{version}.txt";

    internal static bool TryNormalizeVersion(string? raw, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            if (!ValidIdentifiers(text[(plus + 1)..], false)) return false;
            text = text[..plus];
        }
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        // 预发布后缀（v0.1.13-beta）只用于标签，不参与数值比较
        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            if (!ValidIdentifiers(text[(dash + 1)..], true)) return false;
            text = text[..dash];
        }

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

    private static bool ValidIdentifiers(string text, bool prerelease)
        => text.Split('.').All(id => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && !(prerelease && id.All(char.IsAsciiDigit) && id.Length > 1 && id[0] == '0'));

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

    /// <summary>下载到新文件；-1 表示长度未知，0–100 表示实际接收进度。清单等小文件只走官方直连。</summary>
    public static Task DownloadAsync(string url, string targetFile, Action<double>? progress,
        CancellationToken cancellationToken = default, long maximumBytes = 512L * 1024 * 1024)
        => DownloadCoreAsync(url, targetFile, progress, cancellationToken, maximumBytes, allowMirror: false, targetFile + ".part");

    /// <summary>
    /// 安装包下载：官方直连断了就留在 .part 上续传，直连反复失败再换加速前缀。
    /// 第三方前缀只影响传输，最终仍以官方 SHA256 清单为准。
    /// </summary>
    internal static Task DownloadPackageAsync(string url, string targetFile, Action<double>? progress,
        CancellationToken cancellationToken = default, long maximumBytes = 512L * 1024 * 1024)
        => DownloadCoreAsync(url, targetFile, progress, cancellationToken, maximumBytes, allowMirror: true,
            targetFile + ".part");

    private const int AttemptsPerSource = 3;

    private static async Task DownloadCoreAsync(string url, string targetFile, Action<double>? progress,
        CancellationToken cancellationToken, long maximumBytes, bool allowMirror, string partial)
    {
        if (!UpdateMirrors.IsAllowed(url) || (!allowMirror && !IsOfficialUrl(url)))
            throw new InvalidOperationException(Str.T("Str.UpdateUrlInvalid"));

        var candidates = UpdateMirrors.ResolveCandidates(url, allowMirror).ToArray();
        Exception? failure = null;
        foreach (var candidate in candidates)
        {
            if (!UpdateMirrors.IsAllowed(candidate)) continue;
            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await DownloadOnceAsync(candidate, partial, progress, cancellationToken, maximumBytes))
                    {
                        progress?.Invoke(100);
                        if (File.Exists(targetFile)) File.Delete(targetFile);
                        File.Move(partial, targetFile);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) { failure = ex; }
                if (attempt < AttemptsPerSource)
                    await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw failure ?? new IOException(Str.T("Str.UpdateDownloadIncomplete"));
    }

    /// <summary>一次传输；返回 false 表示连接在拿到完整长度前断了，可继续重试（.part 保留已收字节）。</summary>
    private static async Task<bool> DownloadOnceAsync(string url, string partial, Action<double>? progress,
        CancellationToken cancellationToken, long maximumBytes)
    {
        var already = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        using var client = new HttpClient();
        // 总时限与用户取消由上面的 linked token 统一控制。
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FpsTune/1.0");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (already > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(already, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        var total = -1L;
        var append = false;
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && already > 0)
        {
            append = true;
            total = response.Content.Headers.ContentRange?.Length ?? -1;
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && already > 0)
        {
            // .part 比服务端文件还长：丢掉重来。
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            total = response.Content.Headers.ContentLength ?? -1;
        }
        if (total > maximumBytes) throw new InvalidOperationException(Str.T("Str.UpdateFileTooLarge"));

        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = append ? already : 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = 0;
        progress?.Invoke(total > 0 ? written * 100.0 / total : -1);
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            if (written + read > maximumBytes) throw new InvalidOperationException(Str.T("Str.UpdateFileTooLarge"));
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            written += read;
            if (total > 0 && (watch.ElapsedMilliseconds - lastReport >= 100 || written == total))
            {
                progress?.Invoke(written * 100.0 / total);
                lastReport = watch.ElapsedMilliseconds;
            }
        }
        // 服务端没给长度（分块传输）时以流结束为准，不能当作失败。
        if (total < 0) return true;
        return written == total;
    }
}
