using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record RemovedGame(StateStore.GameProfile Profile, string Json);

/// <summary>Library edits preserve unknown fields, take a write lease, and keep the previous file.</summary>
internal static class GameLibraryService
{
    internal static IReadOnlyList<StateStore.GameProfile> Read() => Profiles(ReadDocument(out _));
    private static JsonArray ReadDocument(out string? original)
    {
        original = null;
        _ = StateStore.LoadGames(); // Existing legacy migration stays authoritative.
        var path = StateStore.GamesFilePath;
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new JsonException();
            original = File.ReadAllText(path, Encoding.UTF8);
            var array = JsonNode.Parse(original) as JsonArray ?? throw new JsonException();
            _ = Profiles(array); return array;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException(Str.T("Str.LibraryUnreadable"), ex); }
    }
    private static List<StateStore.GameProfile> Profiles(JsonArray array)
    {
        var profiles = array.Deserialize<List<StateStore.GameProfile>>() ?? throw new JsonException();
        if (profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 80 || !p.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || string.IsNullOrWhiteSpace(p.Name)
                || !Path.IsPathFullyQualified(p.ExePath) || !p.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            || profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count
            || profiles.Select(p => p.ExePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count)
            throw new JsonException();
        return profiles;
    }
    private static T Edit<T>(Func<JsonArray, T> edit)
    {
        var file = StateStore.GamesFilePath; var directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.ChangeExtension(file, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var document = ReadDocument(out var before); var initial = document.ToJsonString();
        var result = edit(document);
        _ = Profiles(document);
        if (initial == document.ToJsonString()) return result;
        if (before != (File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null)) throw new IOException(Str.T("Str.LibraryConflict"));
        if (before is not null)
        {
            var backup = Path.Combine(directory, "game-library-backups", $"games-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            AtomicFile.WriteAllTextDurable(backup, before, new UTF8Encoding(false));
        }
        AtomicFile.WriteAllTextDurable(file, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return result;
    }
    internal static StateStore.GameProfile Add(string path)
    {
        BackupService.EnsureExecutablePath(path.Trim()); path = Path.GetFullPath(path.Trim());
        return Edit(document =>
        {
            var existing = Profiles(document).FirstOrDefault(p => p.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            var profile = new StateStore.GameProfile("g" + Guid.NewGuid().ToString("N")[..8], path, Path.GetFileNameWithoutExtension(path), DateTime.Now);
            document.Add(JsonSerializer.SerializeToNode(profile)); return profile;
        });
    }
    private static int Locate(JsonArray document, StateStore.GameProfile expected)
    {
        var profiles = Profiles(document); var index = profiles.FindIndex(p => p.Id == expected.Id);
        if (index < 0 || profiles[index] != expected) throw new IOException(Str.T("Str.LibraryConflict"));
        return index;
    }
    internal static void Rename(StateStore.GameProfile expected, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException(Str.T("Str.LibraryNameInvalid"));
        Edit(document => { document[Locate(document, expected)]![nameof(StateStore.GameProfile.Name)] = name; return true; });
    }
    internal static RemovedGame Remove(StateStore.GameProfile expected) => Edit(document =>
    {
        var index = Locate(document, expected); var removed = new RemovedGame(expected, document[index]!.ToJsonString());
        document.RemoveAt(index); return removed;
    });
    internal static void UndoRemove(RemovedGame removed) => Edit(document =>
    {
        if (Profiles(document).Any(p => p.Id == removed.Profile.Id || p.ExePath.Equals(removed.Profile.ExePath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException(Str.T("Str.LibraryConflict"));
        document.Add(JsonNode.Parse(removed.Json)); return true;
    });
}
