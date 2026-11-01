using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 竞技调校的"配置档"。哪些游戏有调校、用哪个档、显示什么文案，全部来自
/// <c>catalog/games.json</c> 的 <c>tuning</c> 字段；这个枚举只保留"档"本身的行为。
/// </summary>
internal enum CompetitiveGame { DeltaForce, Valorant, Cs2, Csgo, Ue }

internal static class GameTuning
{
    /// <summary>
    /// 老的三款按可执行名兜底：即使 catalog 里丢了 tuning 字段，CS2/无畏契约/三角洲
    /// 也不会突然失去调校（本轮的数据化不允许以功能回退为代价）。
    /// </summary>
    private static CompetitiveGame? LegacyIdentify(string fileName) => fileName switch
    {
        "deltaforceclient-win64-shipping.exe" or "deltaforceclient.exe" => CompetitiveGame.DeltaForce,
        "valorant-win64-shipping.exe" => CompetitiveGame.Valorant,
        "cs2.exe" => CompetitiveGame.Cs2,
        _ => null
    };

    private static CompetitiveGame? FromProfile(string profile) => profile switch
    {
        "deltaforce" => CompetitiveGame.DeltaForce,
        "valorant" => CompetitiveGame.Valorant,
        "cs2" => CompetitiveGame.Cs2,
        "csgo" => CompetitiveGame.Csgo,
        "ue" => CompetitiveGame.Ue,
        _ => null
    };

    /// <summary>可执行名 → 调校档；没有调校配置的游戏返回 null（页签据此隐藏）。</summary>
    internal static CompetitiveGame? Identify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        if (GameCatalog.TuningFor(fileName) is { } spec && FromProfile(spec.Profile) is { } fromCatalog)
            return fromCatalog;
        return LegacyIdentify(fileName);
    }

    private static TuningSpec? SpecFor(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : GameCatalog.TuningFor(Path.GetFileName(path));

    internal static string Guide(CompetitiveGame game) => Str.T(game switch
    { CompetitiveGame.DeltaForce => "Str.TuningGuideDeltaForce", CompetitiveGame.Valorant => "Str.TuningGuideValorant", _ => "Str.TuningGuideCs2" });

    internal static string Scene(CompetitiveGame game) => Str.T(game switch
    { CompetitiveGame.DeltaForce => "Str.TuningSceneDeltaForce", CompetitiveGame.Valorant => "Str.TuningSceneValorant", _ => "Str.TuningSceneCs2" });

    internal static string Source(CompetitiveGame game) => game switch
    {
        CompetitiveGame.Valorant => "https://support.riotgames.com/en-us/valorant/support-tools/multithreaded-rendering/",
        CompetitiveGame.Cs2 => "https://help.steampowered.com/en/faqs/view/418E-7A04-B0DA-9032",
        _ => "https://www.nvidia.com/en-us/geforce/technologies/reflex/supported-products/"
    };

    /// <summary>
    /// 按游戏取文案：catalog 里给了专属文案就用它（中英并列，走 Str.Pick，不占资源字典），
    /// 否则回落到该档的既有资源键——老的三款因此保持输出不变。
    /// </summary>
    internal static string Guide(string? path)
        => SpecFor(path) is { } spec && !string.IsNullOrWhiteSpace(spec.Guide)
            ? Str.Pick(spec.Guide, spec.GuideEn)
            : Identify(path) is { } game ? Guide(game) : "";

    internal static string Scene(string? path)
        => SpecFor(path) is { } spec && !string.IsNullOrWhiteSpace(spec.Scene)
            ? Str.Pick(spec.Scene, spec.SceneEn)
            : Identify(path) is { } game ? Scene(game) : "";

    internal static string Source(string? path)
        => SpecFor(path) is { Source: { Length: > 0 } source } ? source
            : Identify(path) is { } game ? Source(game) : "";

    /// <summary>通用 UE 档下 %LOCALAPPDATA% 的候选目录名；其他档由既有分支固定，不用这个。</summary>
    internal static IReadOnlyList<string> ConfigRoots(string? path)
        => SpecFor(path)?.ConfigRoots ?? [];

    /// <summary>是否显示"控制台限帧命令"页签：只有 CS2 有 fps_max_ui。</summary>
    internal static bool SupportsConsoleCommands(CompetitiveGame game) => game == CompetitiveGame.Cs2;

    internal static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToUpperInvariant())));
    internal static string Cs2Commands(string foreground, string menu)
    {
        if (!uint.TryParse(foreground.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            || !uint.TryParse(menu.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            || !DisplayQualityService.IsValidFrameLimit(a) || !DisplayQualityService.IsValidFrameLimit(b))
            throw new ArgumentException(Str.T("Str.TuningCommandRange"));
        return $"fps_max {a}; fps_max_ui {b}";
    }
    internal static SessionConditions RecordingConditions(GameVideoConfig config, CompetitiveGame game, int seconds, string? gamePath = null)
    {
        if (seconds is not (60 or 120 or 180)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var hasWidth = int.TryParse(config.Values.GetValueOrDefault("width"), out var width);
        var hasHeight = int.TryParse(config.Values.GetValueOrDefault("height"), out var height);
        var quality = Str.T("Str.TuningRecordingQuality", config.DisplayValue("mode"), config.DisplayValue("vsync"));
        return new(Width: hasWidth && hasHeight ? width : null, Height: hasWidth && hasHeight ? height : null,
            Quality: quality, Scene: gamePath is null ? Scene(game) : Scene(gamePath), TargetSeconds: seconds);
    }
}

internal sealed record GameConfigBaseline(int SchemaVersion, string GameKey, string ConfigKey, DateTimeOffset SavedAt, Dictionary<string, string> Values);

internal static class GameConfigBaselines
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static string FileFor(string game, string config) => Path.Combine(UserDataPaths.Root, "game-tuning", GameTuning.Key(game) + "-" + GameTuning.Key(config) + ".json");
    internal static GameConfigBaseline? Load(string game, string config)
    {
        var path = FileFor(game, config);
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException();
            var baseline = JsonSerializer.Deserialize<GameConfigBaseline>(File.ReadAllText(path), Json);
            if (baseline is not { SchemaVersion: 1, Values: { Count: <= 7 } } || baseline.GameKey != GameTuning.Key(game)
                || baseline.ConfigKey != GameTuning.Key(config) || baseline.SavedAt == default
                || !GameVideoConfig.ValidValues(baseline.Values)) throw new InvalidDataException();
            return baseline;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new InvalidDataException(Str.T("Str.TuningBaselineUnreadable"), ex); }
    }
    internal static void Save(string game, GameVideoConfig config)
    {
        var path = FileFor(game, config.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _ = Load(game, config.Path);
        var baseline = new GameConfigBaseline(1, GameTuning.Key(game), GameTuning.Key(config.Path), DateTimeOffset.Now, new(config.Values));
        AtomicFile.WriteAllTextDurable(path, JsonSerializer.Serialize(baseline, Json), new UTF8Encoding(false));
    }
}
