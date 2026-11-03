using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>规则匹配依据。Path 精确匹配完整 exe；ProcessName 匹配主程序名（含启动器），可附加标题。</summary>
public enum FilterAppMatchKind { ProcessName, Path }

/// <summary>
/// 在某台程序上应用的画面参数快照。应用栏把这些参数应用到某个游戏时存进规则，
/// 之后前台命中就直接按这份值写屏；null = 用预设自带的<b>ForPreset</b> 默认值。
/// </summary>
public sealed record AppRuleScreen(
    double Brightness = 0, double Contrast = 100, double Gamma = 1, double Shadows = 0,
    double Red = 100, double Green = 100, double Blue = 100, double Highlight = 0,
    double DarkBalance = ScreenColorService.DefaultDarkBalance, bool DarkBalanceEnabled = false)
{
    public ScreenColorSettings ToSettings()
        => new(Brightness, Contrast, Gamma, Shadows, Red, Green, Blue, Highlight, DarkBalance, DarkBalanceEnabled);

    public static AppRuleScreen FromSettings(ScreenColorSettings settings)
        => new(settings.Brightness, settings.Contrast, settings.Gamma, settings.Shadows,
            settings.Red, settings.Green, settings.Blue, settings.Highlight, settings.DarkBalance, settings.DarkBalanceEnabled);

    /// <summary>序列化回来的值可能被手改过：超出取值范围就不接受，让规则回落到预设默认。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid
    {
        get { try { ToSettings().Validate(); return true; } catch (ArgumentOutOfRangeException) { return false; } }
    }
}

/// <summary>
/// 独立游戏方案的规则。预设同时支持内置 .icc 与 generated: 枚举；振动和画面参数各自可选。
/// Enabled=false 保留规则但跳过；IncludeScreen=true 才应用预设附带的 gamma 画面参数。
/// Auto=true 是「自动扫描收编」来的规则：它跟着全自动模板走，用户在应用栏单独改它就转成手动。
/// </summary>
public sealed record FilterAppRule(
    string ProcessName,
    string PresetFileName,
    int? Vibrance = null,
    bool IncludeScreen = false,
    string ExePath = "",
    string TitleContains = "",
    bool Enabled = true,
    FilterAppMatchKind MatchKind = FilterAppMatchKind.ProcessName,
    bool Auto = false,
    AppRuleScreen? Screen = null)
{
    /// <summary>旧 schema 没有规则 ID；以规范化匹配条件作为稳定身份，不用“→”拆显示字符串。</summary>
    public string Key => string.Join("|", MatchKind, NormalizedTarget, TitleContains.Trim().ToLowerInvariant());

    /// <summary>这条规则会不会真的改画面；false = 跟随桌面设置（应用时不写任何东西）。</summary>
    public bool HasEffect => PresetFileName.Length > 0 || Vibrance is not null || IncludeScreen;

    internal string NormalizedTarget => MatchKind == FilterAppMatchKind.Path
        ? IccAppRuleStore.NormalizePath(ExePath)
        : IccAppRuleStore.NormalizeProcess(ProcessName);
}

public sealed record ForegroundAppInfo(string ProcessName, string ExePath = "", string WindowTitle = "", int ProcessId = 0);

public static class FilterAppRuleMatcher
{
    public static FilterAppRule? Match(IEnumerable<FilterAppRule> rules, ForegroundAppInfo? app)
    {
        if (app is null) return null;
        var process = IccAppRuleStore.NormalizeProcess(app.ProcessName);
        var path = IccAppRuleStore.NormalizePath(app.ExePath);
        var title = app.WindowTitle.Trim();
        // 完整路径更具体，因此同名启动器与游戏本体可走不同方案。
        return rules.Where(r => r.Enabled).Select(r => (Rule: r, Kind: r.MatchKind))
            .OrderByDescending(x => x.Kind == FilterAppMatchKind.Path)
            .ThenByDescending(x => x.Rule.TitleContains.Trim().Length > 0)
            .Select(x => x.Rule)
            .FirstOrDefault(rule =>
            {
                if (rule.TitleContains.Trim() is { Length: > 0 } needle
                    && !title.Contains(needle, StringComparison.CurrentCultureIgnoreCase)) return false;
                return rule.MatchKind == FilterAppMatchKind.Path
                    ? path.Length > 0 && path == rule.NormalizedTarget
                    : process.Length > 0 && process == rule.NormalizedTarget;
            });
    }
}

/// <summary>独立游戏方案 JSON 存储、schema 迁移及有界导入导出；不写 ICC/DVC/gamma。</summary>
public static class IccAppRuleStore
{
    private sealed record RuleFile(int SchemaVersion, List<FilterAppRule> Rules);
    private const int SchemaVersion = 3;
    public const int MaxRules = 64;
    private const int MaxImportBytes = 256 * 1024;
    private const int MaxTextLength = 4096;

    private static readonly object Gate = new();
    private static List<FilterAppRule>? _cache;
    internal static Func<string>? FileOverride { get; set; }
    internal static void Invalidate() { lock (Gate) _cache = null; }

    private static string FilePath => FileOverride?.Invoke()
        ?? Path.Combine(UserDataPaths.Root, "game-filters", "app-rules.json");

    public static IReadOnlyList<FilterAppRule> Rules
    {
        get { lock (Gate) return _cache ??= Load(FilePath); }
    }

    public static void Save(IReadOnlyList<FilterAppRule> rules)
    {
        var clean = Clean(rules);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllTextDurable(FilePath,
                JsonSerializer.Serialize(new RuleFile(SchemaVersion, clean)), new System.Text.UTF8Encoding(false));
            _cache = clean;
        }
    }

    /// <summary>兼容旧调用（进程名）。有路径信息的探针必须走 Match(ForegroundAppInfo)。</summary>
    public static FilterAppRule? Match(string processName)
        => FilterAppRuleMatcher.Match(Rules, new ForegroundAppInfo(processName));

    public static FilterAppRule? Match(ForegroundAppInfo? app)
        => FilterAppRuleMatcher.Match(Rules, app);

    public static string ExportJson()
        => JsonSerializer.Serialize(new RuleFile(SchemaVersion, Clean(Rules)), new JsonSerializerOptions { WriteIndented = true });

    public static void Export(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllTextDurable(path, ExportJson(), new System.Text.UTF8Encoding(false));
    }

    /// <summary>先完整验证，再原子替换；无效导入不会清掉现有规则。</summary>
    public static IReadOnlyList<FilterAppRule> Import(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxImportBytes) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
        var clean = Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        Save(clean);
        return clean;
    }

    public static void Remove(string key)
        => Save(Rules.Where(r => r.Key != key).ToList());

    private static List<FilterAppRule> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > MaxImportBytes) return [];
            return Clean(Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<FilterAppRule> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RuleFile>(json);
        if (file is null || file.SchemaVersion is < 1 or > SchemaVersion || file.Rules is null)
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        return Clean(file.Rules);
    }

    private static List<FilterAppRule> Clean(IEnumerable<FilterAppRule> rules)
    {
        var result = new List<FilterAppRule>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.ProcessName) && string.IsNullOrWhiteSpace(rule.ExePath)) continue;
            var preset = rule.PresetFileName ?? "";
            var title = rule.TitleContains ?? "";
            // 「还没设滤镜」的规则要能留住：手动添加 exe、以及「重置为桌面设置」都会产生这种规则，
            // 它们在列表里显示为未设滤镜、进程序时什么都不做（跟随桌面）。
            if (preset.Length > 260
                || (rule.ProcessName ?? "").Length > MaxTextLength || (rule.ExePath ?? "").Length > MaxTextLength
                || title.Length > 120 || rule.Vibrance is < 0 or > 100) continue;
            var normalized = rule with
            {
                ProcessName = NormalizeProcess(rule.ProcessName),
                PresetFileName = preset.Trim(),
                // 存原始大小写给界面看；匹配与去重走 NormalizedTarget（大小写不敏感）。
                ExePath = DisplayPath(rule.ExePath) is { Length: > 0 } displayPath ? displayPath : NormalizePath(rule.ExePath),
                TitleContains = title.Trim(),
                // 画面快照是「锦上添花」：越界就丢掉，退回预设默认值，别让一条坏数据把整个规则废掉。
                Screen = rule.Screen is { IsValid: true } screen ? screen : null,
                Auto = rule.Auto,
            };
            // 显式路径规则必须给完整路径；进程名规则只保存 basename，避免 UI 输入全路径后静默错配。
            if (normalized.MatchKind == FilterAppMatchKind.Path)
            {
                if (normalized.ExePath.Length == 0) continue;
                if (normalized.ProcessName.Length == 0) normalized = normalized with { ProcessName = Path.GetFileName(normalized.ExePath) };
            }
            else if (normalized.ProcessName.Length == 0) continue;
            if (keys.Add(normalized.Key)) result.Add(normalized);
            if (result.Count == MaxRules) break;
        }
        return result;
    }

    internal static string NormalizeProcess(string? name)
    {
        var trimmed = (name ?? "").Trim().Trim('"');
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl)) return "";
        var file = Path.GetFileName(trimmed);
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file += ".exe";
        return file.ToLowerInvariant();
    }

    internal static string NormalizePath(string? path)
    {
        var text = (path ?? "").Trim().Trim('"').Replace('/', '\\');
        if (text.Length == 0 || text.Any(char.IsControl) || !Path.IsPathRooted(text)
            || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
        try { return Path.GetFullPath(text).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    /// <summary>
    /// 保存进规则里的路径：补全成完整路径但**保留原始大小写**，界面上的「已保存的程序」才不是一串小写。
    /// 匹配与去重仍走 <see cref="NormalizePath"/>（大小写不敏感），两者互不影响。
    /// </summary>
    internal static string DisplayPath(string? path)
    {
        var text = (path ?? "").Trim().Trim('"').Replace('/', '\\');
        if (text.Length == 0 || text.Any(char.IsControl) || !Path.IsPathRooted(text)
            || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
        try { return Path.GetFullPath(text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}
