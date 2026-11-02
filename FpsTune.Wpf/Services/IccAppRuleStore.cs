using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>规则匹配依据。Path 精确匹配完整 exe；ProcessName 匹配主程序名（含启动器），可附加标题。</summary>
public enum FilterAppMatchKind { ProcessName, Path }

/// <summary>
/// 独立游戏方案的规则。预设同时支持内置 .icc 与 generated: 枚举；振动和画面参数各自可选。
/// Enabled=false 保留规则但跳过；IncludeScreen=true 才应用预设附带的 gamma 画面参数。
/// </summary>
public sealed record FilterAppRule(
    string ProcessName,
    string PresetFileName,
    int? Vibrance = null,
    bool IncludeScreen = false,
    string ExePath = "",
    string TitleContains = "",
    bool Enabled = true,
    FilterAppMatchKind MatchKind = FilterAppMatchKind.ProcessName)
{
    /// <summary>旧 schema 没有规则 ID；以规范化匹配条件作为稳定身份，不用“→”拆显示字符串。</summary>
    public string Key => string.Join("|", MatchKind, NormalizedTarget, TitleContains.Trim().ToLowerInvariant());

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
    private const int SchemaVersion = 2;
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
            if (preset.Length > 260 || (preset.Length == 0 && rule.Vibrance is null && !rule.IncludeScreen)
                || (rule.ProcessName ?? "").Length > MaxTextLength || (rule.ExePath ?? "").Length > MaxTextLength
                || title.Length > 120 || rule.Vibrance is < 0 or > 100) continue;
            var normalized = rule with
            {
                ProcessName = NormalizeProcess(rule.ProcessName),
                PresetFileName = preset.Trim(),
                ExePath = NormalizePath(rule.ExePath),
                TitleContains = title.Trim(),
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
}
