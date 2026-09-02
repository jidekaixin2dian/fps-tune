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

    [GeneratedRegex(@"(?:\\\\\?\\)?[A-Za-z]:\\[^\r\n;,""'\]\)<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathRegex();

    [GeneratedRegex(@"[A-Za-z]:/[^\r\n;,""'\]\)<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex SlashPathRegex();

    [GeneratedRegex(@"(?:\\\\\?\\)?\\\\[^\r\n;,""'\]\)<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathRegex();

    /// <summary>替换绝对路径和账号标识；路径匹配允许空格，避免只脱掉到 Program 为止。</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var result = text;

        // 先吞掉完整路径，再处理普通文本中的目录/用户名，避免留下
        // "&lt;user&gt;\\Documents\\..." 这样的半截绝对路径。
        result = UncPathRegex().Replace(result, PathPlaceholder);
        result = SlashPathRegex().Replace(result, PathPlaceholder);
        result = DrivePathRegex().Replace(result, PathPlaceholder);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            result = result.Replace(localAppData, "<localappdata>", StringComparison.OrdinalIgnoreCase);

        var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profileDir) && profileDir.Length > 3)
            result = result.Replace(profileDir, UserPlaceholder, StringComparison.OrdinalIgnoreCase);

        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            // 账号名可能出现在任意路径或文本中；隐私优先于保留同名普通词。
            result = result.Replace(userName, UserPlaceholder, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }
}
