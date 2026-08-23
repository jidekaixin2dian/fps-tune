using System.IO;
using System.Text;
using System.Text.Json;

namespace DeltaForceTune.Wpf.Services;

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
        "DeltaForceTune");

    private static string SettingsFile => Path.Combine(BaseDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                Current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            if (Current.QQLink.Contains("wpa.qq.com") && !string.IsNullOrWhiteSpace(Current.QQ))
                Current.QQLink = "tencent://message/?uin=" + Uri.EscapeDataString(Current.QQ) + "&Site=qq&Menu=yes";
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        // 自动生成常用联系方式链接
        if (string.IsNullOrWhiteSpace(settings.QQLink) && !string.IsNullOrWhiteSpace(settings.QQ))
            settings.QQLink = "tencent://message/?uin=" + Uri.EscapeDataString(settings.QQ) + "&Site=qq&Menu=yes";

        // 微信没有官方可靠的“直接聊天”URL，不自动生成；点击时会复制微信号。
        // 如果你有自定义链接/二维码页面，可以在设置里手动填写。

        Current = settings;
        Directory.CreateDirectory(BaseDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFile, json, new UTF8Encoding(false));
    }
}
