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
    public string OverviewMode { get; set; } = "studio";
    // 首页"继续使用"：最近访问页面键（不含 home），最多 6 个。
    public List<string> RecentPages { get; set; } = [];
    // 滤镜列表上次选的分类键（all/soft/deexpose/bright/color/game/face），下次打开沿用。
    public string FilterCategory { get; set; } = "all";
    public CornerPreferences Corners { get; set; } = new();
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool WallpaperEnabled { get; set; }
    public double WallpaperOpacity { get; set; } = 0.18;
    public GamePreparationOptions Preparation { get; set; } = new();
    public EqualizerSettings? EqualizerDraft { get; set; }
    public MemoryAutoCleanupOptions MemoryAutoCleanup { get; set; } = new();
    public List<string> MemoryBackgroundApps { get; set; } = [];
    public bool AutoRecordEnabled { get; set; }
    public List<string> AutoRecordGames { get; set; } = [];
    public HudAppearance HudAppearance { get; set; } = new();

    // 托盘常驻（v1.2 起）：关闭去向（X / Alt+F4）与全局热键、完成通知沿用各自设置；
    // 最小化自 0.4.0 起回归常规语义（任务栏），是否随最小化进托盘改为显式 opt-in。
    public bool MinimizeToTray { get; set; }

    // 一次性迁移标记（版本号）：旧默认"最小化进托盘"翻转回任务栏；此后用户手动开启不再迁移。
    public string TrayMinimizeMigration { get; set; } = "";
    public string CloseAction { get; set; } = CloseBehavior.Ask;
    public bool HotkeyEnabled { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;

    // 更新完成后「这个版本改了什么」的弹窗只弹一次：记下已经看过的版本。
    public string WhatsNewSeenVersion { get; set; } = "";

    // 低配模式: 减弱动效与阴影、拉长采样间隔
    public bool LowSpecMode { get; set; }

    // 按游戏自动应用：仅按进程名轮询，不读取进程路径或进程内存。
    public bool AutoProfileEnabled { get; set; }
    public List<AutoProfileBinding> AutoProfileBindings { get; set; } = new();

    // 游戏运行时保持 0.5 ms 计时器分辨率（opt-in，默认关闭）。
    public bool GameTimerBoostEnabled { get; set; }

    // 游戏自动化（opt-in，默认关闭）：启动边沿触发，不做持续监控写操作。
    public bool AutoPreparationEnabled { get; set; }
    public bool AutoRefreshBoostEnabled { get; set; }
    // 游戏时后台应用让路：按用户在维护页挑选的后台应用清单，隔离到效率核并降优先级。
    public bool AutoAffinityIsolateEnabled { get; set; }
    // 独立程序滤镜守护（opt-in，默认关闭）：前台进程命中规则时切滤镜 ICC。
    public bool FilterWatcherEnabled { get; set; }
    // 关闭后保持：关掉主窗口不退出，留在托盘里继续跟随（默认关）。
    public bool FilterWatcherKeepAfterExit { get; set; }
    // 进游戏时提示：自动套上滤镜时弹一次托盘气泡（默认关）。
    public bool FilterApplyNotify { get; set; }
    // 切出游戏就恢复（默认开＝一直沿用的前台跟随）；关掉后改成跟进程走，切到桌面也保持，进程退出才还原。
    public bool FilterRestoreOnFocusLoss { get; set; } = true;
    // 没收录到的游戏也自动应用：按下面的范围当场判断，用「全自动模板」套上。
    public bool FilterAutoFallbackEnabled { get; set; }
    // 全自动范围：fullscreen（全屏游戏）/ gamedir（仅游戏目录）/ any（所有前台程序）。
    public string FilterAutoScope { get; set; } = "fullscreen";

    // 全屏自动切换画面方案（opt-in，默认关闭）：游戏进全屏时套用选定方案，退出全屏还原。
    // 与上面的进程规则守护互斥：两个都开时只生效全屏方案。
    public bool FullscreenPresetEnabled { get; set; }
    // gamma = 游戏内生效（默认，走显示器 LUT）；icc = 仅色彩管理应用感知。
    public string FullscreenPresetChannel { get; set; } = "gamma";
    // 方案名取 IccFilterPreset 的枚举名（Vivid / ShadowBoost / …）；空 = 未选。
    public string FullscreenPresetName { get; set; } = "";
    // 只对 catalog/games.json 里登记的可执行文件生效，避免看视频/全屏浏览器被误切。
    public bool FullscreenPresetOnlyForGames { get; set; } = true;
    // 外置准星（opt-in，默认关闭）：覆盖窗 + AW 战术据枪（按住右键隐藏）。
    public bool CrosshairEnabled { get; set; }
    public string CrosshairStyle { get; set; } = "cross";
    public string CrosshairColor { get; set; } = "#00FF66";
    public double CrosshairSize { get; set; } = 12;
    public double CrosshairGap { get; set; } = 4;
    public double CrosshairThickness { get; set; } = 2;
    public double CrosshairOpacity { get; set; } = 90;
    public bool CrosshairAdaptiveHide { get; set; } = true;

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
    private static string BaseDir => UserDataPaths.Root;

    private static string SettingsFile => Path.Combine(BaseDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    private static AppSettings CreateDefault() => new()
    {
        WeChat = "jiaxindeyang",
        QQ = "1335638265",
        Douyin = "jiaxindeyang",
        Email = "",
        WeChatLink = "",
        QQLink = ProductLinks.CommunityUrl,
        DouyinLink = "https://v.douyin.com/sf-DA6eLcjQ/",
        ThemeMode = "system"
    };

    /// <summary>
    /// 启动时读不到配置文件（IO/占用/权限）时置位：本轮按默认值运行，但**拒绝回写**，
    /// 否则用户下一次点任何开关都会用默认值覆盖掉磁盘上真正的设置。
    /// </summary>
    internal static bool SettingsFileUnreadable { get; private set; }

    /// <summary>启动读取/解析失败时给界面用的一句话（null = 一切正常）。</summary>
    internal static string? LoadWarning { get; private set; }

    public static void Load()
    {
        SettingsFileUnreadable = false;
        LoadWarning = null;
        try
        {
            if (!File.Exists(SettingsFile))
            {
                Current = CreateDefault();
                return;
            }

            Current = ReadWithRetry();
            Current.AutoProfileBindings ??= new List<AutoProfileBinding>();
            Current.Preparation ??= new();
            Current.MemoryAutoCleanup = (Current.MemoryAutoCleanup ?? new()).Normalize();
            Current.MemoryBackgroundApps ??= [];
            Current.AutoRecordGames = AutoRecordingService.NormalizePaths(Current.AutoRecordGames).ToList();
            Current.HudAppearance = (Current.HudAppearance ?? new()).Normalize();
            Current.Corners = (Current.Corners ?? new()).Normalize();
            Current.CloseAction = CloseBehavior.Normalize(Current.CloseAction);
            Current.WallpaperOpacity = WallpaperService.NormalizeOpacity(Current.WallpaperOpacity);
            if (Current.OverviewMode != "console") Current.OverviewMode = "studio";
            if ((Current.QQLink.Contains("wpa.qq.com") || Current.QQLink.Contains("tencent://")) && !string.IsNullOrWhiteSpace(Current.QQ))
                Current.QQLink = "https://user.qzone.qq.com/" + Uri.EscapeDataString(Current.QQ);
            MigrateTrayMinimizeDefault(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 「读不出来」不等于「没有配置」：被杀毒软件占着、被别的进程锁着、权限不足都属这一类。
            // 这时既不要留档成损坏文件（会误导），也不能按默认值回写（会真的把用户设置覆盖掉）。
            Current = CreateDefault();
            SettingsFileUnreadable = true;
            LoadWarning = ex.Message;
        }
        catch
        {
            AtomicFile.PreserveCorrupt(SettingsFile);
            Current = CreateDefault();
        }
    }

    /// <summary>设置文件偶尔会被瞬时占用（杀软扫描/同步盘），读三次再判定失败。</summary>
    private static AppSettings ReadWithRetry()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(80);
            }
        }
        throw last!;
    }

    public const string TrayMinimizeMigrationVersion = "0.4.0";

    /// <summary>
    /// 0.4.0 一次性迁移：旧默认"最小化进托盘"回归任务栏最小化。
    /// 标记存迁移版本号（空 = 未迁移）；完成后用户在设置里手动开启不会被再次翻转。
    /// </summary>
    private static void MigrateTrayMinimizeDefault(AppSettings settings)
    {
        if (settings.TrayMinimizeMigration == TrayMinimizeMigrationVersion) return;
        settings.TrayMinimizeMigration = TrayMinimizeMigrationVersion;
        settings.MinimizeToTray = false;
        try { Save(settings); }
        catch { /* 写回失败时下次启动重试；本次会话已按任务栏最小化运行 */ }
    }

    public static void Save(AppSettings settings)
    {
        // 自动生成常用个人主页链接
        if (string.IsNullOrWhiteSpace(settings.QQLink) && !string.IsNullOrWhiteSpace(settings.QQ))
            settings.QQLink = "https://user.qzone.qq.com/" + Uri.EscapeDataString(settings.QQ);

        // 微信个人微信号没有公开主页，不能自动生成；点击时会复制微信号。
        // 如果你有自定义个人主页/二维码页面，可以在设置里手动填写。

        Current = settings;
        // 启动时原始文件读不出来：这一轮只在内存里生效，绝不把默认值覆盖回磁盘。
        // （会话中用户仍能改，界面按新值工作；下次启动读到原文件时设置会「回退」，比丢配置好。）
        if (SettingsFileUnreadable) return;
        Directory.CreateDirectory(BaseDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(SettingsFile, json, new UTF8Encoding(false));
    }
}
