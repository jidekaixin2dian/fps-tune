using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsTune.Wpf.Services;

internal enum CompetitiveGame { DeltaForce, Valorant, Cs2 }

internal static class GameTuning
{
    internal static CompetitiveGame? Identify(string? path) => Path.GetFileName(path ?? "").ToLowerInvariant() switch
    {
        "deltaforceclient-win64-shipping.exe" or "deltaforceclient.exe" => CompetitiveGame.DeltaForce,
        "valorant-win64-shipping.exe" => CompetitiveGame.Valorant,
        "cs2.exe" => CompetitiveGame.Cs2,
        _ => null
    };
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
    internal static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToUpperInvariant())));
    internal static string Cs2Commands(string foreground, string menu)
    {
        if (!uint.TryParse(foreground.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            || !uint.TryParse(menu.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            || !DisplayQualityService.IsValidFrameLimit(a) || !DisplayQualityService.IsValidFrameLimit(b))
            throw new ArgumentException(Str.T("Str.TuningCommandRange"));
        return $"fps_max {a}; fps_max_ui {b}";
    }
    internal static SessionConditions RecordingConditions(GameVideoConfig config, CompetitiveGame game, int seconds)
    {
        if (seconds is not (60 or 120 or 180)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var hasWidth = int.TryParse(config.Values.GetValueOrDefault("width"), out var width);
        var hasHeight = int.TryParse(config.Values.GetValueOrDefault("height"), out var height);
        var quality = Str.T("Str.TuningRecordingQuality", config.DisplayValue("mode"), config.DisplayValue("vsync"));
        return new(Width: hasWidth && hasHeight ? width : null, Height: hasWidth && hasHeight ? height : null,
            Quality: quality, Scene: Scene(game), TargetSeconds: seconds);
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
