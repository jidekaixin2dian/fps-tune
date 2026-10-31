using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Core;

internal sealed record KnownGame(string Name, string[] Executables, string[] Directories);

/// <summary>Executable identities and install names have one embedded, reviewable data source.</summary>
internal static class GameCatalog
{
    internal static IReadOnlyList<KnownGame> All { get; } = Load();
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
        }
        return Array.AsReadOnly(games);
    }
}
