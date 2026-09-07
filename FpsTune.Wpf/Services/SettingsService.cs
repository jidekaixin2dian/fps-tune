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
    public string OverviewMode { get; set; } = "console";

    // 托盘常驻（v1.2）：最小化到托盘 / 全局热键呼出 / 完成通知，默认全开
    public bool MinimizeToTray { get; set; } = true;
    public bool HotkeyEnabled { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;

    // 界面氛围: 整窗三层极光光场，默认开
    public bool AuroraEnabled { get; set; } = true;

    // 低配模式: 减弱动效与阴影、拉长采样间隔
    public bool LowSpecMode { get; set; }

    // 按游戏自动应用：仅按进程名轮询，不读取进程路径或进程内存。
    public bool AutoProfileEnabled { get; set; }
    public List<AutoProfileBinding> AutoProfileBindings { get; set; } = new();

    public bool HasContact =>
        !string.IsNullOrWhiteSpace(WeChat) ||
        !string.IsNullOrWhiteSpace(QQ) ||
        !string.IsNullOrWhiteSpace(Douyin) ||
        !string.IsNullOrWhiteSpace(Email);
}

/// <summary>按进程名触发配置方案的最小持久化映射。</summary>
public sealed class AutoProfileBinding
{
    public string DisplayName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public AutoProfileBinding Clone() => new()
    {
        DisplayName = DisplayName,
        ProcessName = ProcessName,
        ExePath = ExePath,
        ProfileName = ProfileName,
        Enabled = Enabled
    };

    /// <summary>Process.GetProcessesByName 使用不带 .exe 的规范名。</summary>
    public static string NormalizeProcessName(string? value)
    {
        var text = (value ?? "").Trim().Trim('"');
        if (text.Length == 0)
            return "";

        var name = Path.GetFileName(text);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Trim();
    }
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
            Current.AutoProfileBindings ??= new List<AutoProfileBinding>();
            if ((Current.QQLink.Contains("wpa.qq.com") || Current.QQLink.Contains("tencent://")) && !string.IsNullOrWhiteSpace(Current.QQ))
                Current.QQLink = "https://user.qzone.qq.com/" + Uri.EscapeDataString(Current.QQ);
        }
        catch
        {
            AtomicFile.PreserveCorrupt(SettingsFile);
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
        AtomicFile.WriteAllText(SettingsFile, json, new UTF8Encoding(false));
    }
}
