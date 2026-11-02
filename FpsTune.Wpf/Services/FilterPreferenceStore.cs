using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>排序使用的轻量项，避免服务层反向依赖 WPF 的 FilterChoice。</summary>
public sealed record FilterPreferenceChoice(string FileName);

/// <summary>常用滤镜与最近使用；独立于 AppSettings，避免收藏操作重写整份设置。</summary>
public static class FilterPreferenceStore
{
    private sealed record PreferenceFile(int SchemaVersion, List<string> Favorites, List<string> Recent);
    private const int MaxRecent = 8;
    private static readonly object Gate = new();
    private static PreferenceFile? _cache;
    internal static Func<string>? FileOverride { get; set; }
    private static string PathName => FileOverride?.Invoke() ?? System.IO.Path.Combine(UserDataPaths.Root, "game-filters", "preferences.json");

    public static IReadOnlyList<string> Favorites => Load().Favorites;
    public static bool IsFavorite(string fileName) => Load().Favorites.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    public static bool ToggleFavorite(string fileName)
    {
        lock (Gate)
        {
            var current = Load();
            var favorites = current.Favorites.Where(x => !x.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
            var favorite = favorites.Count == current.Favorites.Count;
            if (favorite) favorites.Add(fileName);
            Save(new(1, favorites, current.Recent));
            return favorite;
        }
    }

    public static void NoteUsed(string fileName)
    {
        lock (Gate)
        {
            var current = Load();
            var recent = current.Recent.Where(x => !x.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
            recent.Insert(0, fileName);
            Save(new(1, current.Favorites, recent.Take(MaxRecent).ToList()));
        }
    }

    /// <summary>常用优先，其次最近使用；只调整排序，不改变筛选结果。</summary>
    public static IEnumerable<FilterPreferenceChoice> Rank(IEnumerable<FilterPreferenceChoice> choices)
    {
        var state = Load();
        return choices.OrderByDescending(c => state.Favorites.Contains(c.FileName, StringComparer.OrdinalIgnoreCase))
            .ThenBy(c => state.Recent.IndexOf(c.FileName) is var i && i >= 0 ? i : int.MaxValue);
    }

    public static void Invalidate() { lock (Gate) _cache = null; }
    private static PreferenceFile Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                var data = File.Exists(PathName) && new FileInfo(PathName).Length <= 65536
                    ? JsonSerializer.Deserialize<PreferenceFile>(File.ReadAllText(PathName)) : null;
                _cache = data is { SchemaVersion: 1, Favorites: not null, Recent: not null }
                    ? data with
                    {
                        Favorites = data.Favorites.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 260).Distinct(StringComparer.OrdinalIgnoreCase).Take(76).ToList(),
                        Recent = data.Recent.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 260).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecent).ToList(),
                    }
                    : new(1, [], []);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _cache = new(1, [], []); }
            return _cache;
        }
    }

    private static void Save(PreferenceFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        AtomicFile.WriteAllTextDurable(PathName, JsonSerializer.Serialize(file), new System.Text.UTF8Encoding(false));
        _cache = file;
    }
}
