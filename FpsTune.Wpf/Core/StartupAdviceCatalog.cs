using System.IO;

namespace FpsTune.Wpf.Core;

/// <summary>自启项归属的大类，用来一眼看出「这条到底是游戏平台、云盘还是更新器」。</summary>
public enum StartupCategory
{
    Game,
    Accelerator,
    Cloud,
    Peripheral,
    Security,
    Media,
    Updater,
    Other
}

/// <summary>对一条自启项的建议。</summary>
public enum StartupSuggestion
{
    /// <summary>用不到就关：关掉不影响游戏与日常使用。</summary>
    CanDisable,

    /// <summary>看用法：关掉会丢掉某个明确功能（宏、灯效、云同步）。</summary>
    Depends,

    /// <summary>别关：安全、反作弊或游戏平台登录链路。</summary>
    Keep
}

/// <summary>
/// 一条分类规则：命中任意关键字就归到这一类。
/// 关键字只在我们自己拼出来的「名字 + 命令 + 可执行文件名」里做小写包含匹配，
/// 不做正则、不联网，也不读第三方配置。
/// </summary>
internal sealed record StartupRule(string[] Tokens, StartupCategory Category, StartupSuggestion Suggestion, string ReasonKey);

/// <summary>自启项分类与建议清单（我们自己的内容）。</summary>
internal static class StartupAdviceCatalog
{
    /// <summary>顺序即优先级：先认安全与反作弊，再认游戏平台，避免「Vanguard」被当成普通更新器。</summary>
    internal static readonly IReadOnlyList<StartupRule> Rules =
    [
        new(["vanguard", "vgtray", "anticheat", "anti-cheat", "ace-tray", "securityhealth", "defender"],
            StartupCategory.Security, StartupSuggestion.Keep, "Str.StartupWhySecurity"),

        new(["tntcloud", "xunyou", "biubiu", "uu.exe", "uqiu", "qiqi", "jiasu"],
            StartupCategory.Accelerator, StartupSuggestion.Depends, "Str.StartupWhyAccelerator"),

        new(["steam", "epicgames", "riot", "battlenet", "battle.net", "origin", "eaapp", "ubisoft", "uplay",
                "galaxy", "wegame", "gameviewer", "playnite", "xbox"],
            StartupCategory.Game, StartupSuggestion.Keep, "Str.StartupWhyGame"),

        new(["razer", "logi", "lghub", "steelseries", "corsair", "armoury", "icue", "hyperx", "roccat",
                "synapse", "ghub", "openrgb", "wacom"],
            StartupCategory.Peripheral, StartupSuggestion.Depends, "Str.StartupWhyPeripheral"),

        new(["baidu", "onedrive", "aliyun", "quark", "dropbox", "googledrive", "google drive", "nutstore", "jianguoyun",
                "tianyi", "cloud sync", "pan.exe"],
            StartupCategory.Cloud, StartupSuggestion.CanDisable, "Str.StartupWhyCloud"),

        new(["cloudmusic", "spotify", "qqmusic", "kugou", "netease", "music"],
            StartupCategory.Media, StartupSuggestion.CanDisable, "Str.StartupWhyMedia"),

        new(["updater", "update", "autolaunch", "auto-launch", "install", "setup"],
            StartupCategory.Updater, StartupSuggestion.CanDisable, "Str.StartupWhyUpdater")
    ];

    private static readonly StartupRule Fallback = new([], StartupCategory.Other, StartupSuggestion.Depends, "Str.StartupWhyUnknown");

    /// <summary>按名字、命令与可执行文件名判定分类；都没命中就是「其他」。</summary>
    internal static StartupRule Classify(StartupEntry entry)
    {
        var haystack = string.Join('\n', new[]
        {
            entry.Name,
            entry.Command,
            entry.Executable is { Length: > 0 } path ? Path.GetFileName(path) : ""
        }).ToLowerInvariant();

        foreach (var rule in Rules)
        {
            if (rule.Tokens.Any(token => haystack.Contains(token, StringComparison.Ordinal)))
                return rule;
        }
        return Fallback;
    }

    internal static string CategoryLabelKey(StartupCategory category) => category switch
    {
        StartupCategory.Game => "Str.StartupCategoryGame",
        StartupCategory.Accelerator => "Str.StartupCategoryAccelerator",
        StartupCategory.Cloud => "Str.StartupCategoryCloud",
        StartupCategory.Peripheral => "Str.StartupCategoryPeripheral",
        StartupCategory.Security => "Str.StartupCategorySecurity",
        StartupCategory.Media => "Str.StartupCategoryMedia",
        StartupCategory.Updater => "Str.StartupCategoryUpdater",
        _ => "Str.StartupCategoryOther"
    };

    internal static string SuggestionLabelKey(StartupSuggestion suggestion) => suggestion switch
    {
        StartupSuggestion.CanDisable => "Str.StartupSuggestionDisable",
        StartupSuggestion.Depends => "Str.StartupSuggestionDepends",
        _ => "Str.StartupSuggestionKeep"
    };
}
