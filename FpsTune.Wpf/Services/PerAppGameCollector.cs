using System.IO;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>全自动的识别范围：只有落在范围里的程序才会被「没收录也自动应用」套上模板。</summary>
public enum PerAppAutoScope { Fullscreen, GameDirectory, AnyApp }

/// <summary>
/// 自动扫描/收编与全自动范围判定。扫描只读卸载注册表与常见安装目录（<see cref="GamePathService"/>），
/// 不读运行中进程、不写系统；收编写的是我们自己的规则 JSON。
/// </summary>
public static class PerAppGameCollector
{
    /// <summary>一条扫描结果；<see cref="Tracked"/> 表示已经有规则管着（就不再收编）。</summary>
    public sealed record CollectedGame(string Name, string ExePath, bool Tracked);

    /// <summary>扫描本机已安装的已知游戏；refresh=true 走完整重扫（用户点「重新扫描并收编」时）。</summary>
    public static IReadOnlyList<CollectedGame> Scan(IReadOnlyList<FilterAppRule> rules, bool refresh = false)
    {
        var tracked = new HashSet<string>(
            rules.Select(r => IccAppRuleStore.NormalizePath(r.ExePath)).Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var trackedProcesses = new HashSet<string>(
            rules.Where(r => r.MatchKind == FilterAppMatchKind.ProcessName)
                .Select(r => IccAppRuleStore.NormalizeProcess(r.ProcessName)),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<CollectedGame>();
        foreach (var candidate in GamePathService.DetectAll(refresh))
        {
            if (string.IsNullOrWhiteSpace(candidate.ExePath) || !File.Exists(candidate.ExePath)) continue;
            var path = IccAppRuleStore.NormalizePath(candidate.ExePath);
            if (path.Length == 0) continue;
            var process = IccAppRuleStore.NormalizeProcess(candidate.ExePath);
            var isTracked = tracked.Contains(path) || trackedProcesses.Contains(process);
            result.Add(new CollectedGame(candidate.Name, Path.GetFullPath(candidate.ExePath), isTracked));
        }
        // 同一个 exe 在多个盘符兜底扫描里会重复出现，按路径去重。
        return result.GroupBy(g => g.ExePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>把还没规则的游戏收编成「自动」规则；返回新增条数（规则总数有上限，满了就停）。</summary>
    public static int Collect(IEnumerable<CollectedGame> games, FilterAutoTemplate template)
    {
        var rules = IccAppRuleStore.Rules.ToList();
        var added = 0;
        foreach (var game in games)
        {
            if (game.Tracked) continue;
            if (rules.Count >= IccAppRuleStore.MaxRules) break;
            // 存原始大小写的完整路径（界面要显示它），匹配时再按大小写不敏感比较。
            var display = IccAppRuleStore.DisplayPath(game.ExePath);
            if (display.Length == 0) continue;
            var rule = BuildRule(display, template);
            if (rules.Any(r => r.Key == rule.Key)) continue;
            rules.Add(rule);
            added++;
        }
        if (added > 0) IccAppRuleStore.Save(rules);
        return added;
    }

    /// <summary>模板变了：所有「自动」规则跟着改；手动规则一条都不动。</summary>
    public static int ApplyTemplate(FilterAutoTemplate template)
    {
        var rules = IccAppRuleStore.Rules;
        if (!rules.Any(r => r.Auto)) return 0;
        var updated = rules.Select(r => r.Auto
            ? r with
            {
                PresetFileName = template.PresetFileName,
                Vibrance = template.Vibrance,
                IncludeScreen = template.IncludeScreen,
                Screen = template.Screen,
            }
            : r).ToList();
        IccAppRuleStore.Save(updated);
        return updated.Count(r => r.Auto);
    }

    /// <summary>已收编（自动）的条数，界面上的「全自动 · 已收录 N 个」用它。</summary>
    public static int CollectedCount(IReadOnlyList<FilterAppRule> rules) => rules.Count(r => r.Auto);

    private static FilterAppRule BuildRule(string displayPath, FilterAutoTemplate template)
        => new(Path.GetFileName(displayPath), template.PresetFileName, template.Vibrance, template.IncludeScreen,
            displayPath, "", true, FilterAppMatchKind.Path, Auto: true, Screen: template.Screen);
}

/// <summary>范围判定：给「没收录到的游戏也自动应用」用，真实按 exe 路径与全屏状态判断。</summary>
public static class PerAppAutoScopeRules
{
    /// <summary>Steam/Epic/WeGame 等库和引擎目录；命中就算「游戏目录里的程序」。</summary>
    private static readonly string[] GameLibraryHints =
    [
        @"\steamapps\common\", @"\SteamLibrary\", @"\steamapps\", @"\Epic Games\", @"\WeGameApps\",
        @"\Tencent Games\", @"\Riot Games\", @"\Ubisoft\", @"\GOG Games\", @"\EA Games\", @"\XboxGames\",
        @"\UnrealEngine\", @"\UE4\", @"\UE5\", @"\GOG Galaxy\Games\",
    ];

    public static PerAppAutoScope Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "gamedir" => PerAppAutoScope.GameDirectory,
        "any" => PerAppAutoScope.AnyApp,
        _ => PerAppAutoScope.Fullscreen,
    };

    public static string Key(PerAppAutoScope scope) => scope switch
    {
        PerAppAutoScope.GameDirectory => "gamedir",
        PerAppAutoScope.AnyApp => "any",
        _ => "fullscreen",
    };

    /// <summary>exe 是否落在游戏库/引擎目录，或者是目录里登记过的游戏主程序。</summary>
    public static bool IsGameDirectory(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var name = Path.GetFileName(exePath);
        if (name.Length > 0 && GamePathService.IsKnownGameExecutable(name)) return true;
        if (GameLibraryHints.Any(hint => exePath.Contains(hint, StringComparison.OrdinalIgnoreCase))) return true;
        // 目录名提示（三角洲、CS2 之类）只做包含匹配，路径里出现就算，不做磁盘访问。
        return GameCatalog.All.Any(game => game.Directories.Any(directory =>
            directory.Length > 0 && exePath.Contains(directory, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>范围命中：全屏档要求真全屏 + 像游戏；游戏目录档只看路径；所有前台程序档除自己都算。</summary>
    public static bool Matches(PerAppAutoScope scope, string? exePath, bool isFullscreen)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        if (Path.GetFileName(exePath).Equals("FpsTune.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return scope switch
        {
            PerAppAutoScope.AnyApp => true,
            PerAppAutoScope.GameDirectory => IsGameDirectory(exePath),
            _ => isFullscreen && IsGameDirectory(exePath),
        };
    }

    /// <summary>前台窗口是不是全屏（全屏档的判据）。读不到就当作不是，避免误套。</summary>
    internal static Func<bool>? FullscreenOverride { get; set; }

    public static bool IsForegroundFullscreen()
    {
        if (FullscreenOverride is { } hook) return hook();
        try
        {
            var state = new FullscreenDetector(new Win32ForegroundProbe()).Evaluate(out _, out _);
            return state is ForegroundScreenState.FullscreenBorderless or ForegroundScreenState.FullscreenExclusive;
        }
        catch { return false; }
    }
}
