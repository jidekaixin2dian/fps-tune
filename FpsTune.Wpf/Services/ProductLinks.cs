using System.Diagnostics;

namespace FpsTune.Wpf.Services;

internal static class ProductLinks
{
    internal const string CommunityUrl = "https://qm.qq.com/q/jwbacnxrmU";
    internal const string IssueUrl = "https://github.com/jidekaixin2dian/fps-tune/issues/new";

    internal static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    internal static string ReleasePage(UpdateInfo info)
    {
        const string prefix = "/jidekaixin2dian/fps-tune/releases/tag/";
        if (!UpdateService.IsOfficialUrl(info.Url)) throw new ArgumentException("Invalid release page.");
        var uri = new Uri(info.Url);
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Invalid release page.");
        var tag = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !UpdateService.TryNormalizeVersion(tag, out var version)
            || !UpdateService.TryNormalizeVersion(info.Version, out var expected) || version != expected
            || (info.ReleaseTag is not null && tag != info.ReleaseTag))
            throw new ArgumentException("Invalid release page.");
        return uri.AbsoluteUri;
    }
}
