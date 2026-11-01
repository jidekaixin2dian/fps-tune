using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>独立程序滤镜规则：进程主程序名（小写 .exe）→ 滤镜预设文件名。</summary>
public sealed record FilterAppRule(string ProcessName, string PresetFileName);

/// <summary>
/// 独立程序滤镜规则表（JSON 持久化，原子写）。前台守护按"主程序文件名"匹配，
/// 与自动录制/自动档案同姿态：不读进程内存，仅凭可执行文件名识别。
/// </summary>
public static class IccAppRuleStore
{
    private sealed record RuleFile(int SchemaVersion, List<FilterAppRule> Rules);
    private const int SchemaVersion = 1;
    public const int MaxRules = 20;

    private static readonly object Gate = new();
    private static List<FilterAppRule>? _cache;
    internal static Func<string>? FileOverride { get; set; }

    private static string FilePath => FileOverride?.Invoke()
        ?? Path.Combine(UserDataPaths.Root, "game-filters", "app-rules.json");

    public static IReadOnlyList<FilterAppRule> Rules
    {
        get
        {
            lock (Gate)
            {
                if (_cache is { } cached) return cached;
                _cache = Load();
                return _cache;
            }
        }
    }

    public static void Save(IReadOnlyList<FilterAppRule> rules)
    {
        lock (Gate)
        {
            var clean = rules
                .Where(r => r is not null
                    && !string.IsNullOrWhiteSpace(r.ProcessName)
                    && !string.IsNullOrWhiteSpace(r.PresetFileName))
                .Select(r => r with { ProcessName = NormalizeProcess(r.ProcessName) })
                .Where(r => r.ProcessName.Length > 0)
                .DistinctBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRules).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllTextDurable(FilePath,
                JsonSerializer.Serialize(new RuleFile(SchemaVersion, clean)), new System.Text.UTF8Encoding(false));
            _cache = clean;
        }
    }

    /// <summary>按前台进程主程序名查规则；未命中返回 null。</summary>
    public static FilterAppRule? Match(string processName)
    {
        var key = NormalizeProcess(processName);
        if (key.Length == 0) return null;
        return Rules.FirstOrDefault(r => r.ProcessName.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeProcess(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl)) return "";
        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) trimmed += ".exe";
        return trimmed is not ['"', ..] && !trimmed.Contains('\\') && !trimmed.Contains('/') ? trimmed.ToLowerInvariant() : Path.GetFileName(trimmed).ToLowerInvariant();
    }

    private static List<FilterAppRule> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            if (new FileInfo(FilePath).Length > 64 * 1024) throw new InvalidDataException();
            var file = JsonSerializer.Deserialize<RuleFile>(File.ReadAllText(FilePath, System.Text.Encoding.UTF8));
            if (file is not { SchemaVersion: SchemaVersion, Rules: { } rules }) return [];
            return rules.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.ProcessName)).Take(MaxRules).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return [];
        }
    }
}
