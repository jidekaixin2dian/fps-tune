using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

/// <summary>Read only, known scalar fields; never executes config text or rewrites game files.</summary>
internal sealed record GameVideoConfig(string Path, DateTime LastWrite, string Sha256, Dictionary<string, string> Values)
{
    internal static readonly string[] Fields = ["width", "height", "mode", "vsync", "fps", "refresh"];
    internal string DisplayValue(string key) => DisplayValue(key, Values.GetValueOrDefault(key));
    internal static string DisplayValue(string key, string? value) => value is null ? Str.T("Str.TuningUnknown") : key switch
    {
        "mode" => Str.T(value switch { "Exclusive" => "Str.TuningModeExclusive", "Borderless" => "Str.TuningModeBorderless", "Windowed" => "Str.TuningModeWindowed", _ => "Str.TuningUnknown" }),
        "vsync" => Str.T(value == "1" ? "Str.TuningEnabled" : "Str.Off"),
        "fps" => value == "0" ? Str.T("Str.FrameOff") : value + " FPS",
        "refresh" => value + " Hz", _ => value
    };
    internal static bool ValidValues(Dictionary<string, string> values) => values.Count is > 0 and <= 6 && values.All(pair =>
        pair.Key switch
        {
            "mode" => pair.Value is "Exclusive" or "Borderless" or "Windowed",
            "vsync" => pair.Value is "0" or "1",
            "width" => int.TryParse(pair.Value, out var w) && w is >= 320 and <= 16384,
            "height" => int.TryParse(pair.Value, out var h) && h is >= 200 and <= 16384,
            "fps" or "refresh" => double.TryParse(pair.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
                && double.IsFinite(n) && n >= (pair.Key == "fps" ? 0 : 20) && n <= 1000,
            _ => false
        });
    internal static string Label(string key) => Str.T(key switch
    {
        "width" => "Str.TuningFieldwidth", "height" => "Str.TuningFieldheight", "mode" => "Str.TuningFieldmode",
        "vsync" => "Str.TuningFieldvsync", "fps" => "Str.TuningFieldfps", _ => "Str.TuningFieldrefresh"
    });

    internal static GameVideoConfig Read(string path, CompetitiveGame game)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 1024 * 1024) throw new InvalidDataException(Str.T("Str.TuningConfigUnreadable"));
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException(Str.T("Str.TuningConfigUnreadable"));
        var values = Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), game);
        info.Refresh();
        return new(System.IO.Path.GetFullPath(path), info.LastWriteTime, Convert.ToHexString(SHA256.HashData(bytes)), values);
    }

    internal static Dictionary<string, string> Parse(string text, CompetitiveGame game)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string value)
        {
            if (raw.TryGetValue(key, out var old) && old != value) conflicts.Add(key);
            else raw[key] = value;
        }
        if (game == CompetitiveGame.Cs2)
        {
            foreach (var line in text.Split('\n'))
            {
                var match = Regex.Match(line, "^\\s*\"(?<key>[^\"\\\\]+)\"\\s+\"(?<value>[^\"\\\\]+)\"\\s*(?://.*)?$", RegexOptions.CultureInvariant);
                if (match.Success) Add(match.Groups["key"].Value, match.Groups["value"].Value);
            }
        }
        else
        {
            var section = "";
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line; continue; }
                var expected = game == CompetitiveGame.DeltaForce ? "[/Script/Engine.GameUserSettings]" : "[/Script/ShooterGame.ShooterGameUserSettings]";
                if (section != expected || line.StartsWith(';') || line.StartsWith('#')) continue;
                var split = line.IndexOf('='); if (split > 0) Add(line[..split].Trim(), line[(split + 1)..].Trim());
            }
        }
        foreach (var key in conflicts) raw.Remove(key);
        var result = new Dictionary<string, string>();
        string? Get(string ini, string cs) => raw.GetValueOrDefault(game == CompetitiveGame.Cs2 ? cs : ini);
        void Number(string field, string? value, double min, double max)
        {
            if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number) && number >= min && number <= max && (field is not ("width" or "height") || number == Math.Truncate(number)))
                result[field] = number.ToString("0.###", CultureInfo.InvariantCulture);
        }
        Number("width", Get("ResolutionSizeX", "setting.defaultres"), 320, 16384);
        Number("height", Get("ResolutionSizeY", "setting.defaultresheight"), 200, 16384);
        Number("fps", Get("FrameRateLimit", "fps_max"), 0, 1000);
        var sync = Get("bUseVSync", "setting.mat_vsync");
        if (sync is "0" or "1") result["vsync"] = sync;
        else if (bool.TryParse(sync, out var vsync)) result["vsync"] = vsync ? "1" : "0";
        var mode = Get("FullscreenMode", "setting.fullscreen");
        if (game == CompetitiveGame.Cs2)
        {
            if (mode == "1") result["mode"] = "Exclusive";
            else if (mode == "0" && raw.GetValueOrDefault("setting.nowindowborder") is { } border && border is "0" or "1")
                result["mode"] = border == "1" ? "Borderless" : "Windowed";
            if (double.TryParse(raw.GetValueOrDefault("setting.refreshrate_numerator"), CultureInfo.InvariantCulture, out var numerator)
                && double.TryParse(raw.GetValueOrDefault("setting.refreshrate_denominator"), CultureInfo.InvariantCulture, out var denominator)
                && denominator > 0) Number("refresh", (numerator / denominator).ToString(CultureInfo.InvariantCulture), 20, 1000);
        }
        else if (mode is "0" or "1" or "2") result["mode"] = mode switch { "0" => "Exclusive", "1" => "Borderless", _ => "Windowed" };
        if (result.Count == 0) throw new InvalidDataException(Str.T("Str.TuningConfigUnreadable"));
        return result;
    }

    internal static IReadOnlyList<string> Discover(string gamePath, CompetitiveGame game, string? localRoot = null, IEnumerable<string>? steamRoots = null)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path) { if (File.Exists(path)) candidates.Add(System.IO.Path.GetFullPath(path)); }
        localRoot ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (game == CompetitiveGame.Valorant)
        {
            var root = System.IO.Path.Combine(localRoot, "VALORANT", "Saved", "Config");
            Add(System.IO.Path.Combine(root, "WindowsClient", "GameUserSettings.ini"));
            if (Directory.Exists(root)) foreach (var directory in Directory.EnumerateDirectories(root).Take(128))
                Add(System.IO.Path.Combine(directory, "WindowsClient", "GameUserSettings.ini"));
        }
        else if (game == CompetitiveGame.DeltaForce)
        {
            var roots = new List<string> { System.IO.Path.Combine(localRoot, "DeltaForce") };
            for (var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(gamePath)!); dir is not null && roots.Count < 7; dir = dir.Parent) roots.Add(dir.FullName);
            foreach (var root in roots) foreach (var platform in new[] { "WindowsClient", "WindowsNoEditor" })
                Add(System.IO.Path.Combine(root, "Saved", "Config", platform, "GameUserSettings.ini"));
        }
        else
        {
            if (steamRoots is null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steamRoots = new[] { key?.GetValue("SteamPath") as string, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") }
                    .Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>();
            }
            foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var users = System.IO.Path.Combine(root, "userdata"); if (!Directory.Exists(users)) continue;
                foreach (var user in Directory.EnumerateDirectories(users).Take(128)) Add(System.IO.Path.Combine(user, "730", "local", "cfg", "cs2_video.txt"));
            }
        }
        return candidates.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
