using System.IO;
using System.Text;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

public sealed class AppSettings
{
    public string WeChat { get; set; } = "";
    public string QQ { get; set; } = "";
    public string Douyin { get; set; } = "";
    public string Email { get; set; } = "";
    public string WeChatLink { get; set; } = "";
    public string QQLink { get; set; } = "";
    public string DouyinLink { get; set; } = "";
    public string ThemeMode { get; set; } = "dark";

    public bool HasContact =>
        !string.IsNullOrWhiteSpace(WeChat) ||
        !string.IsNullOrWhiteSpace(QQ) ||
        !string.IsNullOrWhiteSpace(Douyin) ||
        !string.IsNullOrWhiteSpace(Email);
}

public static class SettingsService
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FpsTune");

    private static string SettingsFile => Path.Combine(BaseDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    private static AppSettings CreateDefault() => new()
    {
        WeChat = "jiaxindeyang",
        QQ = "1335638265",
        Douyin = "jiaxindeyang",
        Email = "",
        WeChatLink = "",
        QQLink = "https://qm.qq.com/q/jwbacnxrmU",
        DouyinLink = "https://v.douyin.com/sf-DA6eLcjQ/",
        ThemeMode = "system"
    };

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                Current = CreateDefault();
                return;
            }

            var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefault();
            if ((Current.QQLink.Contains("wpa.qq.com") || Current.QQLink.Contains("tencent://")) && !string.IsNullOrWhiteSpace(Current.QQ))
                Current.QQLink = "https://user.qzone.qq.com/" + Uri.EscapeDataString(Current.QQ);
        }
        catch
        {
            Current = CreateDefault();
        }
    }

    public static void Save(AppSettings settings)
    {
        // 自动生成常用个人主页链接
        if (string.IsNullOrWhiteSpace(settings.QQLink) && !string.IsNullOrWhiteSpace(settings.QQ))
            settings.QQLink = "https://user.qzone.qq.com/" + Uri.EscapeDataString(settings.QQ);

        // 微信个人微信号没有公开主页，不能自动生成；点击时会复制微信号。
        // 如果你有自定义个人主页/二维码页面，可以在设置里手动填写。

        Current = settings;
        Directory.CreateDirectory(BaseDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFile, json, new UTF8Encoding(false));
    }
}
