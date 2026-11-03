using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>一条存下来的滤镜方案：滤镜预设 + 数字振动 + 是否带画面参数。</summary>
public sealed record FilterScheme(string Id, string Name, string PresetFileName, int? Vibrance, bool IncludeScreen,
    AppRuleScreen? Screen);

/// <summary>「全自动」模板：自动收编的规则和「没收录也自动应用」都按这一套写屏。</summary>
public sealed record FilterAutoTemplate(string PresetFileName, int? Vibrance, bool IncludeScreen, AppRuleScreen? Screen);

/// <summary>
/// 最小的滤镜方案库：用户在应用栏点「存为方案」时把当前滤镜与调色存下来，之后一键切回去。
/// 只存参数（预设名 + 数值），不存 ICC 文件字节；文件放 game-filters/schemes.json，
/// 与 app-rules.json 同目录，两者都是纯 JSON、可导出、坏了不会连坐。
/// </summary>
public static class FilterSchemeStore
{
    public const int MaxSchemes = 8;
    private const int SchemaVersion = 1;
    private const long MaxFileBytes = 128 * 1024;
    private const int MaxNameLength = 40;
    private const int MaxPresetLength = 260;

    private sealed record SchemeFile(int SchemaVersion, List<FilterScheme> Schemes, FilterAutoTemplate? AutoTemplate);

    private static readonly object Gate = new();
    private static SchemeFile? _cache;

    internal static Func<string>? FileOverride { get; set; }

    private static string PathName => FileOverride?.Invoke()
        ?? Path.Combine(UserDataPaths.Root, "game-filters", "schemes.json");

    public static IReadOnlyList<FilterScheme> Schemes
    {
        get { lock (Gate) return Load().Schemes; }
    }

    public static FilterAutoTemplate? AutoTemplate
    {
        get { lock (Gate) return Load().AutoTemplate; }
    }

    /// <summary>同名覆盖，超上限拒绝（不静默丢最旧的一条，那等于替用户删东西）。</summary>
    public static FilterScheme Save(string name, string presetFileName, int? vibrance, bool includeScreen, AppRuleScreen? screen)
    {
        var cleanName = CleanName(name);
        var preset = CleanPreset(presetFileName);
        var snapshot = CleanScreen(screen);
        var percent = CleanVibrance(vibrance);
        lock (Gate)
        {
            var current = Load();
            var existing = current.Schemes.FirstOrDefault(s => s.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase));
            if (existing is null && current.Schemes.Count >= MaxSchemes)
                throw new InvalidOperationException(Str.T("Str.FilterSchemeLimit", MaxSchemes));
            var scheme = new FilterScheme(existing?.Id ?? "s" + Guid.NewGuid().ToString("N")[..8], cleanName,
                preset, percent, includeScreen, snapshot);
            var schemes = current.Schemes.Where(s => !s.Id.Equals(scheme.Id, StringComparison.Ordinal)).ToList();
            var index = existing is null ? schemes.Count : current.Schemes.ToList().FindIndex(s => s.Id == existing.Id);
            schemes.Insert(Math.Clamp(index, 0, schemes.Count), scheme);
            Save(new SchemeFile(SchemaVersion, schemes, current.AutoTemplate));
            return scheme;
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            var current = Load();
            var schemes = current.Schemes.Where(s => !s.Id.Equals(id, StringComparison.Ordinal)).ToList();
            if (schemes.Count == current.Schemes.Count) return;
            Save(new SchemeFile(SchemaVersion, schemes, current.AutoTemplate));
        }
    }

    /// <summary>应用栏选「全部游戏」点应用时写入；收编的规则与「没收录也自动应用」都读它。</summary>
    public static FilterAutoTemplate SaveAutoTemplate(string presetFileName, int? vibrance, bool includeScreen, AppRuleScreen? screen)
    {
        var template = new FilterAutoTemplate(CleanPreset(presetFileName), CleanVibrance(vibrance), includeScreen, CleanScreen(screen));
        lock (Gate) Save(new SchemeFile(SchemaVersion, Load().Schemes, template));
        return template;
    }

    public static void ClearAutoTemplate()
    {
        lock (Gate)
        {
            var current = Load();
            if (current.AutoTemplate is null) return;
            Save(new SchemeFile(SchemaVersion, current.Schemes, null));
        }
    }

    public static FilterScheme? Find(string id)
        => Schemes.FirstOrDefault(s => s.Id.Equals(id, StringComparison.Ordinal));

    internal static void Invalidate() { lock (Gate) _cache = null; }

    private static string CleanName(string name)
    {
        var text = (name ?? "").Trim();
        if (text.Length is 0 or > MaxNameLength || text.Any(char.IsControl))
            throw new ArgumentException(Str.T("Str.FilterSchemeNameInvalid"));
        return text;
    }

    private static string CleanPreset(string preset)
    {
        var text = (preset ?? "").Trim();
        if (text.Length > MaxPresetLength) throw new ArgumentException(Str.T("Str.FilterSchemeNameInvalid"));
        return text;
    }

    private static int? CleanVibrance(int? vibrance) => vibrance is >= 0 and <= 100 ? vibrance : null;

    private static AppRuleScreen? CleanScreen(AppRuleScreen? screen) => screen is { IsValid: true } ? screen : null;

    private static SchemeFile Load()
    {
        lock (Gate)
        {
            if (_cache is { } cached) return cached;
            SchemeFile? file = null;
            try
            {
                if (File.Exists(PathName) && new FileInfo(PathName).Length <= MaxFileBytes)
                    file = JsonSerializer.Deserialize<SchemeFile>(File.ReadAllText(PathName));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                file = null;
            }
            _cache = Normalize(file);
            return _cache;
        }
    }

    private static SchemeFile Normalize(SchemeFile? file)
    {
        var schemes = new List<FilterScheme>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in file?.Schemes ?? [])
        {
            if (scheme is null || scheme.Id is not { Length: > 0 } id || !ids.Add(id)) continue;
            var name = (scheme.Name ?? "").Trim();
            if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl)) continue;
            schemes.Add(scheme with
            {
                Name = name,
                PresetFileName = (scheme.PresetFileName ?? "").Trim(),
                Vibrance = CleanVibrance(scheme.Vibrance),
                Screen = CleanScreen(scheme.Screen),
            });
            if (schemes.Count == MaxSchemes) break;
        }
        var template = file?.AutoTemplate is { } t
            ? new FilterAutoTemplate((t.PresetFileName ?? "").Trim(), CleanVibrance(t.Vibrance), t.IncludeScreen, CleanScreen(t.Screen))
            : null;
        return new SchemeFile(SchemaVersion, schemes, template);
    }

    private static void Save(SchemeFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        AtomicFile.WriteAllTextDurable(PathName, JsonSerializer.Serialize(file), new System.Text.UTF8Encoding(false));
        _cache = file;
    }
}
