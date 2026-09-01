using System.Text.RegularExpressions;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 诊断包隐私脱敏（纯逻辑，可单测）：
/// 用户名、用户目录、本应用数据目录与任意盘符路径统一替换为占位符。
/// </summary>
public static partial class PrivacyScrub
{
    public const string UserPlaceholder = "<user>";
    public const string PathPlaceholder = "<path>";

    [GeneratedRegex(@"[A-Za-z]:\\[^\s;,""'\]\)]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathRegex();

    [GeneratedRegex(@"[A-Za-z]:/[^\s;,""'\]\)]*", RegexOptions.CultureInvariant)]
    private static partial Regex SlashPathRegex();

    /// <summary>按顺序替换：本应用数据目录 → 用户主目录/用户名 → 其余盘符路径。</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var result = text;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            result = result.Replace(localAppData, "<localappdata>", StringComparison.OrdinalIgnoreCase);

        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profileDir) && profileDir.Length > 3)
            result = result.Replace(profileDir, UserPlaceholder, StringComparison.OrdinalIgnoreCase);

        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
        {
            // 账号名可能出现在任意路径或文本中；短用户名（如 "li"）容易误伤，跳过
            result = result.Replace(userName, UserPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        result = SlashPathRegex().Replace(result, PathPlaceholder);
        result = DrivePathRegex().Replace(result, PathPlaceholder);
        return result;
    }
}
