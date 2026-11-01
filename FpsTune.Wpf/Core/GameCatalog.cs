using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 一款游戏的竞技调校配置（可选）。Profile 决定"读哪套配置 + 用哪套页签"，其余字段是该游戏的
/// 专属文案；文案留空时回落到 Profile 的既有资源键，所以老的三款行为一字不变。
///
/// ConfigRoots 是通用 UE 档（profile = ue）下 %LOCALAPPDATA% 的目录名提示。
/// 它必须由数据给出而不是靠猜：猜错只会找不到文件，绝不会读到别的游戏的配置。
/// </summary>
internal sealed record TuningSpec(
    string Profile,
    string[]? ConfigRoots = null,
    string? Guide = null,
    string? GuideEn = null,
    string? Scene = null,
    string? SceneEn = null,
    string? Source = null);

internal sealed record KnownGame(string Name, string[] Executables, string[] Directories, TuningSpec? Tuning = null);

/// <summary>Executable identities and install names have one embedded, reviewable data source.</summary>
internal static class GameCatalog
{
    /// <summary>Profile 的合法取值；新增档位必须同时改 <c>GameTuning</c> 的映射。</summary>
    /// <remarks>必须声明在 <see cref="All"/> 之前：All 的初始化器会调用 Load() → ValidateTuning()，
    /// 而静态字段初始化器按文本顺序执行，排在后面的话这里还是 null。</remarks>
    internal static readonly string[] TuningProfiles = ["cs2", "csgo", "valorant", "deltaforce", "ue"];

    internal static IReadOnlyList<KnownGame> All { get; } = Load();

    /// <summary>可执行名 → 调校配置（大小写不敏感）。没有调校的游戏不在表里。</summary>
    private static readonly Dictionary<string, TuningSpec> TuningByExecutable = BuildTuningIndex(All);

    internal static TuningSpec? TuningFor(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return null;
        return TuningByExecutable.TryGetValue(Path.GetFileName(executableName) ?? "", out var spec) ? spec : null;
    }

    private static Dictionary<string, TuningSpec> BuildTuningIndex(IReadOnlyList<KnownGame> games)
    {
        var index = new Dictionary<string, TuningSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (game.Tuning is null) continue;
            foreach (var exe in game.Executables) index[exe] = game.Tuning;
        }
        return index;
    }

    private static IReadOnlyList<KnownGame> Load()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("FpsTune.GameCatalog.json")
            ?? throw new InvalidDataException("Missing game catalog resource.");
        var games = JsonSerializer.Deserialize<KnownGame[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty game catalog.");
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (string.IsNullOrWhiteSpace(game.Name) || game.Executables is not { Length: > 0 } || game.Directories is not { Length: > 0 })
                throw new InvalidDataException("Incomplete game catalog entry.");
            foreach (var exe in game.Executables)
                if (string.IsNullOrWhiteSpace(exe) || exe.IndexOfAny(['/', '\\']) >= 0 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !executables.Add(exe))
                    throw new InvalidDataException("Invalid or duplicate game executable.");
            if (game.Tuning is { } tuning) ValidateTuning(tuning, game.Name);
        }
        return Array.AsReadOnly(games);
    }

    private static void ValidateTuning(TuningSpec tuning, string gameName)
    {
        if (Array.IndexOf(TuningProfiles, tuning.Profile) < 0)
            throw new InvalidDataException($"Unknown tuning profile for {gameName}.");
        // 目录名提示只允许单层目录名：带分隔符就能指到任意路径，等于绕开"找不到就如实说没找到"的保护。
        foreach (var root in tuning.ConfigRoots ?? [])
            if (string.IsNullOrWhiteSpace(root) || root.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || root is "." or "..")
                throw new InvalidDataException($"Invalid tuning config root for {gameName}.");
        if (tuning.Profile == "ue" && tuning.ConfigRoots is not { Length: > 0 })
            throw new InvalidDataException($"Tuning profile ue requires configRoots for {gameName}.");
    }
}
