using System.IO;
using System.Text;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>更新完成之后要告诉用户「这个版本改了什么」的那份说明。</summary>
internal sealed record WhatsNewNotice(string Version, string Notes);

/// <summary>
/// 更新说明的落盘与「只弹一次」的状态。
///
/// 说明来源只有一条：更新时从 Release 拿到的正文（发布时按“讲人话”写的），
/// 不在这里编内容；没拿到说明就不弹，宁可少弹也不编。
/// </summary>
internal static class WhatsNewService
{
    internal static string? DirectoryOverride;

    private static string FilePath => Path.Combine(DirectoryOverride ?? UserDataPaths.Root, "updates", "whats-new.json");

    /// <summary>安装前记下这次要更新的版本与说明；安装失败时它不会匹配当前版本，自然不弹。</summary>
    internal static void Record(string version, string? notes)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(notes)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var notice = new WhatsNewNotice(Normalize(version), notes.Trim());
            AtomicFile.WriteAllTextDurable(FilePath, JsonSerializer.Serialize(notice), new UTF8Encoding(false));
        }
        catch
        {
            // 记不下来只是少一次提示，不能让更新流程失败。
        }
    }

    /// <summary>当前版本有未读说明时返回它；版本对不上（更新没成功/已看过）就顺手清掉。</summary>
    internal static WhatsNewNotice? Pending()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > 256 * 1024) { TryDelete(); return null; }
            var notice = JsonSerializer.Deserialize<WhatsNewNotice>(File.ReadAllText(FilePath, Encoding.UTF8));
            if (notice is null || string.IsNullOrWhiteSpace(notice.Notes)) { TryDelete(); return null; }
            var version = Normalize(notice.Version);
            if (!string.Equals(version, Normalize(UpdateService.DisplayVersion), StringComparison.OrdinalIgnoreCase)
                || string.Equals(version, SettingsService.Current.WhatsNewSeenVersion, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete();
                return null;
            }
            return notice with { Version = version };
        }
        catch
        {
            TryDelete();
            return null;
        }
    }

    internal static void MarkSeen(WhatsNewNotice notice)
    {
        SettingsService.Current.WhatsNewSeenVersion = Normalize(notice.Version);
        try { SettingsService.Save(SettingsService.Current); } catch { /* 写不了下次再弹一次，不阻断关闭。 */ }
        TryDelete();
    }

    private static string Normalize(string version) => version.Trim().TrimStart('v', 'V');

    private static void TryDelete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
