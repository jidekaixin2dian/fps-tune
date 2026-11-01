using System.IO;
using System.Reflection;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>游戏滤镜单条预设：内置 .icc 资产 + 适用/避坑说明（来源预设标注 "No copyright, use freely"）。
/// Category 为分类键（soft/deexpose/bright/color/game/face），UI 按键映射到本地化组名。
/// <see cref="Vibrance"/> 是这条预设配套的数字振动档（百分比；null = 没有对应值，选中时不动振动滑块）。</summary>
public sealed record GameFilterPreset(string FileName, string Name, string Fit, string Avoid, bool Builtin,
    string Category = "", int? Vibrance = null);

/// <summary>
/// 滤镜中心预设库：内置资产（嵌入资源，随包分发）+ 生成式去曝光预设 + 用户自定义目录（UserDataPaths/GameFilters）。
/// 应用走 IccFilterService.ApplyFromFile（沿用其原值备份/读回/漂移保护契约）。
/// </summary>
public static class GameFilterLibrary
{
    private static IReadOnlyList<GameFilterPreset>? _cache;
    internal static Func<Assembly>? AssemblyOverride { get; set; }
    internal static string? UserDirOverride { get; set; }

    /// <summary>生成式预设文件名前缀（不落盘，应用时即时生成字节）。</summary>
    internal const string GeneratedPrefix = "generated:";

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
                var meta = metadata.TryGetValue(name, out var m) ? m : null;
                list.Add(new(name, PrettyName(name), meta?.Fit ?? "", meta?.Avoid ?? "", Builtin: true,
                    Category: CategoryFor(PrettyName(name)), Vibrance: meta?.Vibrance));
            }
            // 生成式去曝光预设（IccProfileGenerator 即时生成，不占嵌入资产）
            list.Add(new(GeneratedPrefix + nameof(IccFilterPreset.Deexposure), "去曝光 · 轻压", "画面过亮发白、雪地强光", "本来就暗的场景", Builtin: true, Category: "deexpose"));
            list.Add(new(GeneratedPrefix + nameof(IccFilterPreset.DeexposureStrong), "去曝光 · 重压", "强光爆闪、需要深压光", "夜间与暗场", Builtin: true, Category: "deexpose"));
            foreach (var name in ListUser())
            {
                if (!name.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                list.Add(new(name, PrettyName(name), "", "", Builtin: false));
            }
            _cache = list.OrderBy(p => p.FileName, StringComparer.CurrentCulture).ToList();
            return _cache;
        }
    }

    /// <summary>取预设 .icc 字节（生成式 / 内置资产 / 用户目录）。</summary>
    public static byte[] ReadPreset(string fileName)
    {
        if (fileName.StartsWith(GeneratedPrefix, StringComparison.Ordinal))
        {
            var preset = Enum.Parse<IccFilterPreset>(fileName[GeneratedPrefix.Length..], ignoreCase: false);
            return IccProfileGenerator.Build(preset);
        }
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

    /// <summary>presets.json 里每条内置预设的可选元数据。</summary>
    private sealed record PresetMeta(string Fit, string Avoid, int? Vibrance = null);

    private static Dictionary<string, PresetMeta> LoadMetadata()
    {
        try
        {
            using var stream = AssetAssembly.GetManifestResourceStream("FpsTune.GameFilter.presets.json");
            if (stream is null) return [];
            using var reader = new StreamReader(stream);
            var doc = JsonDocument.Parse(reader.ReadToEnd());
            var result = new Dictionary<string, PresetMeta>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string? fit = null, avoid = null;
                int? vibrance = null;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    fit = prop.Value.TryGetProperty("fit", out var f) ? f.GetString() : null;
                    avoid = prop.Value.TryGetProperty("avoid", out var a) ? a.GetString() : null;
                    if (prop.Value.TryGetProperty("vibrance", out var v) && v.ValueKind == JsonValueKind.Number
                        && v.TryGetInt32(out var percent)) vibrance = percent;
                }
                result[prop.Name] = new PresetMeta(fit ?? "", avoid ?? "", vibrance);
            }
            return result;
        }
        catch { return []; }
    }

    /// <summary>文件名 → 展示名（去掉 .icc 扩展与序号前缀，如 "13·日常通用.icc" → "日常通用"）。</summary>
    private static string PrettyName(string fileName)
        => Path.GetFileNameWithoutExtension(fileName);

    private static readonly Dictionary<string, string> CategoryByName = new(StringComparer.Ordinal)
    {
        // 柔和护眼
        ["1.1·柔和"] = "soft", ["5.1·偏灰暗"] = "soft", ["22·柔和3.0"] = "soft", ["46·AZ3柔灰"] = "soft", ["58·雾柔"] = "soft", ["13·日常通用"] = "soft",
        // 压暗去曝
        ["2.1·原色降曝"] = "deexpose", ["15·无曝优化"] = "deexpose", ["28·防雪盲"] = "deexpose", ["12·偏冷灰"] = "deexpose",
        ["41·夜坝"] = "deexpose", ["55·墨影"] = "deexpose", ["54·寒墨"] = "deexpose",
        // 提亮通透
        ["23·高亮2.0"] = "bright", ["24·明透3.0"] = "bright", ["26·白天2.0"] = "bright", ["42·锐透"] = "bright",
        ["43·AZ3清透"] = "bright", ["51·明澈"] = "bright", ["56·清锐"] = "bright", ["47·匀光"] = "bright",
        // 色彩风格
        ["11·偏冷色"] = "color", ["29·琥珀"] = "color", ["30·冰艳"] = "color", ["31·澄蓝"] = "color", ["32·靛蓝"] = "color",
        ["33·粉紫"] = "color", ["34·烈彩"] = "color", ["27·鲜艳2.0"] = "color", ["45·AZ3浓彩"] = "color", ["44·青润"] = "color",
        ["25·电影2.0"] = "color", ["49·AZ3暮色"] = "color", ["14·测试14"] = "color",
        // 游戏专属
        ["36·彩虹6号冷亮"] = "game", ["48·激战2清润"] = "game", ["50·永劫清韵"] = "game", ["52·使命召唤16明晰"] = "game",
        ["53·APEX透彩"] = "game", ["57·原神晴彩"] = "game", ["35·AZ3"] = "game",
        // 人物暗场
        ["16·人物微亮"] = "face", ["17·人物微亮+"] = "face",
        // 会员 ICC（用户解锁的加密模块里取到的正式 profile）
        ["18·特调V1"] = "color", ["19·特调V3"] = "bright", ["20·特调V4"] = "color", ["21·特调V5"] = "deexpose",
        ["37·深艳"] = "color", ["38·浓墨"] = "deexpose", ["39·航天基地"] = "game", ["40·沉冷"] = "deexpose",
    };

    /// <summary>展示名 → 分类键；未收录的归入色彩风格（风格类新增文件不至于落空）。</summary>
    internal static string CategoryFor(string displayName)
        => CategoryByName.TryGetValue(displayName, out var category) ? category : "color";
}
