using System.Text.RegularExpressions;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 更新下载的备用通道。官方直连失败时按顺序试加速前缀；所有候选内容都要用官方
/// SHA256 清单核对，清单本身只从官方直连获取，第三方前缀无法影响完整性校验。
/// 另外从 Release 说明里解析蓝奏云（网盘）地址，供"下载不成功时手动下载"使用。
/// </summary>
internal static partial class UpdateMirrors
{
    /// <summary>加速前缀，按顺序尝试；换成反代只改这一处。</summary>
    private static readonly string[] Prefixes =
    [
        "https://ghfast.top/",
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
        "https://github.moeyy.xyz/",
    ];

    private static readonly string[] PrefixHosts =
        [.. Prefixes.Select(prefix => new Uri(prefix).Host).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>官方地址 + 各加速前缀的候选顺序（第一个总是官方直连）。</summary>
    internal static IEnumerable<string> Candidates(string officialUrl)
    {
        yield return officialUrl;
        if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            yield break;
        foreach (var prefix in Prefixes)
            yield return prefix + officialUrl;
    }

    /// <summary>测试注入：换掉候选源列表（只在本机回环验证续传/回退时用）。</summary>
    internal static Func<string, IEnumerable<string>>? CandidatesOverride;

    /// <summary>测试注入：换掉来源校验（与 <see cref="CandidatesOverride"/> 同时使用）。</summary>
    internal static Func<string, bool>? SourceOverride;

    internal static IEnumerable<string> ResolveCandidates(string url, bool allowMirror)
        => CandidatesOverride is { } custom ? custom(url) : allowMirror ? Candidates(url) : [url];

    internal static bool IsAllowed(string? url) => SourceOverride is { } custom ? custom(url ?? "") : IsAllowedSource(url);

    /// <summary>只允许官方域名或已登记的加速前缀，路径必须落在本项目 Release 资产上。</summary>
    internal static bool IsAllowedSource(string? url)
    {
        if (UpdateService.IsOfficialUrl(url)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        if (!PrefixHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.Contains("/jidekaixin2dian/fps-tune/releases/", StringComparison.Ordinal);
    }

    /// <summary>备用下载：网盘地址 + 可选的访问密码（蓝奏云分享常带密码）。</summary>
    internal sealed record BackupDownload(string Url, string? Password = null);

    /// <summary>从 Release 说明里取第一个蓝奏云/网盘分享地址，并顺带取它旁边的访问密码。</summary>
    internal static BackupDownload? FindBackupLink(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        foreach (Match match in BackupLinkPattern().Matches(notes))
        {
            var text = match.Value.TrimEnd('.', ')', '）', ',', '，', '。', ';', '；', ']', '】', '"', '\'', '>');
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) continue;
            if (!IsBackupHost(uri.Host)) continue;
            if (uri.AbsolutePath.Trim('/').Length < 3) continue;
            return new BackupDownload(text, PasswordFrom(notes, uri));
        }
        return null;
    }

    /// <summary>只取地址（旧调用点的兼容入口）。</summary>
    internal static string? FindBackupDownload(string? notes) => FindBackupLink(notes)?.Url;

    /// <summary>
    /// 密码来源两处：URL 自带的 <c>pwd=</c>/<c>password=</c>，或说明里 URL 附近的"密码: xxx"。
    /// 只认 2–12 位字母数字，避免把整句话当成密码。
    /// </summary>
    private static string? PasswordFrom(string notes, Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;
            var key = pair[..index];
            if (!key.Equals("pwd", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("password", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(pair[(index + 1)..]);
            if (IsPassword(value)) return value;
        }

        // 说明里最靠近该链接的那一处"密码：xxxx"。
        var linkIndex = notes.IndexOf(uri.OriginalString, StringComparison.OrdinalIgnoreCase);
        Match? best = null;
        foreach (Match match in PasswordPattern().Matches(notes))
        {
            if (!IsPassword(match.Groups["pwd"].Value)) continue;
            if (best is null || Math.Abs(match.Index - linkIndex) < Math.Abs(best.Index - linkIndex)) best = match;
        }
        return best?.Groups["pwd"].Value;
    }

    private static bool IsPassword(string value) => value.Length is >= 2 and <= 12 && value.All(char.IsAsciiLetterOrDigit);

    [GeneratedRegex(@"(?:密码|提取码|访问码|pwd|password)\s*[:：=]?\s*(?<pwd>[A-Za-z0-9]{2,12})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    private static readonly string[] BackupHostSuffixes =
    [
        "lanzou.com", "lanzoui.com", "lanzoux.com", "lanzouw.com", "lanzoup.com", "lanzouq.com",
        "lanzoue.com", "lanzouf.com", "lanzoug.com", "lanzouh.com", "lanzouj.com", "lanzouk.com",
        "lanzoul.com", "lanzoum.com", "lanzoun.com", "lanzn.com", "ilanzou.com",
        "pan.quark.cn", "pan.baidu.com",
    ];

    private static bool IsBackupHost(string host)
        => BackupHostSuffixes.Any(suffix => host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"https://[A-Za-z0-9.-]+(?:/[^\s<>""']*)?", RegexOptions.CultureInvariant)]
    private static partial Regex BackupLinkPattern();
}
