using System.IO;
using System.Reflection;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>游戏滤镜单条预设：内置 .icc 资产 + 适用/避坑说明（来源预设标注 "No copyright, use freely"）。</summary>
public sealed record GameFilterPreset(string FileName, string Name, string Fit, string Avoid, bool Builtin);

/// <summary>
/// 滤镜中心预设库：内置资产（嵌入资源，随包分发）+ 用户自定义目录（UserDataPaths/GameFilters）。
/// 应用走 IccFilterService.ApplyFromFile（沿用其原值备份/读回/漂移保护契约）。
/// </summary>
public static class GameFilterLibrary
{
    private static IReadOnlyList<GameFilterPreset>? _cache;
    internal static Func<Assembly>? AssemblyOverride { get; set; }
    internal static string? UserDirOverride { get; set; }

    internal static string UserDir => UserDirOverride
        ?? Path.Combine(UserDataPaths.Root, "game-filters");

    private static Assembly AssetAssembly => AssemblyOverride is { } factory ? factory() : typeof(GameFilterLibrary).Assembly;

    public static IReadOnlyList<GameFilterPreset> Presets
    {
        get
        {
            if (_cache is { } cached) return cached;
            var metadata = LoadMetadata();
            var list = new List<GameFilterPreset>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ListEmbedded())
            {
                if (!name.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                var meta = metadata.TryGetValue(name, out var m) ? ((string Fit, string Avoid)?)m : null;
                list.Add(new(name, PrettyName(name), meta?.Fit ?? "", meta?.Avoid ?? "", Builtin: true));
            }
            foreach (var name in ListUser())
            {
                if (!name.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                list.Add(new(name, PrettyName(name), "", "", Builtin: false));
            }
            _cache = list.OrderBy(p => p.FileName, StringComparer.CurrentCulture).ToList();
            return _cache;
        }
    }

    /// <summary>取预设 .icc 字节（内置资产或用户目录）。</summary>
    public static byte[] ReadPreset(string fileName)
    {
        if (ListEmbedded().Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = AssetAssembly.GetManifestResourceStream("FpsTune.GameFilter." + fileName)
                ?? throw new FileNotFoundException(Str.T("Str.FilterPresetMissing", fileName));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        var userPath = Path.Combine(UserDir, fileName);
        return File.ReadAllBytes(userPath);
    }

    /// <summary>保存用户自定义预设（编辑 ICC 生成结果），返回文件名。</summary>
    public static string SaveUserPreset(string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(UserDir);
        var safe = Path.GetFileName(fileName);
        File.WriteAllBytes(Path.Combine(UserDir, safe), bytes);
        _cache = null;
        return safe;
    }

    public static void Invalidate() => _cache = null;

    private static string[] ListEmbedded()
        => AssetAssembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("FpsTune.GameFilter.", StringComparison.Ordinal) && n.EndsWith(".icc", StringComparison.OrdinalIgnoreCase))
            .Select(n => n["FpsTune.GameFilter.".Length..]).ToArray();

    private static IEnumerable<string> ListUser()
        => Directory.Exists(UserDir) ? Directory.GetFiles(UserDir, "*.icc").Select(Path.GetFileName).OfType<string>() : [];

    private static Dictionary<string, (string Fit, string Avoid)> LoadMetadata()
    {
        try
        {
            using var stream = AssetAssembly.GetManifestResourceStream("FpsTune.GameFilter.presets.json");
            if (stream is null) return [];
            using var reader = new StreamReader(stream);
            var doc = JsonDocument.Parse(reader.ReadToEnd());
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string? fit = null, avoid = null;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    fit = prop.Value.TryGetProperty("fit", out var f) ? f.GetString() : null;
                    avoid = prop.Value.TryGetProperty("avoid", out var a) ? a.GetString() : null;
                }
                result[prop.Name] = (fit ?? "", avoid ?? "");
            }
            return result;
        }
        catch { return []; }
    }

    /// <summary>文件名 → 展示名（去掉 .icc 扩展与序号前缀，如 "13·日常通用.icc" → "日常通用"）。</summary>
    private static string PrettyName(string fileName)
        => Path.GetFileNameWithoutExtension(fileName);
}
