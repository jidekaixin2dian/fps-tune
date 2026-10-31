using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsTune.Wpf.Services;

internal sealed record PreparationRun(string Id, DateTimeOffset StartedAt, double Seconds, string? GameKey,
    string GameName, string Plan, bool Stopped, string? Error, IReadOnlyList<PreparationStep> Steps)
{
    [JsonIgnore] internal bool Succeeded => !Stopped && Error is null && Steps.Count > 0 && Steps.All(s => s.Succeeded);
    [JsonIgnore] internal string Outcome => Str.T(Stopped ? "Str.PrepareStopped" : Succeeded ? "Str.PrepareDone"
        : Steps.Any(s => s.Succeeded) ? "Str.PreparePartial" : "Str.PrepareFailed");
}

/// <summary>Bounded local execution history, separate from authoritative restore receipts.</summary>
internal static class PreparationHistory
{
    private sealed record Document(int SchemaVersion, IReadOnlyList<PreparationRun> Runs);
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string FilePath => Path.Combine(UserDataPaths.Root, "preparation-history", "history.json");

    internal static IReadOnlyList<PreparationRun> Read()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            if (new FileInfo(FilePath).Length > 16 * 1024 * 1024) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath, Encoding.UTF8), Json);
            if (document is not { SchemaVersion: 1, Runs: { Count: <= 50 } runs }
                || runs.Any(r => !Valid(r)) || runs.Select(r => r.Id).Distinct().Count() != runs.Count)
                throw new InvalidDataException();
            return runs;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new InvalidDataException(Str.T("Str.PreparationHistoryUnreadable"), ex); }
    }

    private static bool Valid(PreparationRun? r) => r is not null && Guid.TryParseExact(r.Id, "N", out _)
        && r.StartedAt != default && double.IsFinite(r.Seconds) && r.Seconds is >= 0 and <= 604800
        && (r.GameKey is null || r.GameKey.Length == 64 && r.GameKey.All(char.IsAsciiHexDigit))
        && r.GameName is { Length: <= 200 } && r.Plan is { Length: <= 4096 } && r.Error?.Length is not > 4096
        && r.Steps is { Count: <= 3 } && r.Steps.All(s => s is not null && s.Name is { Length: <= 200 }
            && s.Detail is { Length: <= 16000 } && s.Error?.Length is not > 4096);

    private static string Clean(string? value, int max)
    {
        var text = PrivacyScrub.Sanitize(value);
        return text[..Math.Min(text.Length, max)];
    }
    private static PreparationRun Sanitize(PreparationRun r) => r with
    {
        GameName = Clean(r.GameName, 200), Plan = Clean(r.Plan, 4096), Error = r.Error is null ? null : Clean(r.Error, 4096),
        Steps = r.Steps.Select(s => s with { Name = Clean(s.Name, 200), Detail = Clean(s.Detail, 16000),
            Error = s.Error is null ? null : Clean(s.Error, 4096) }).ToArray()
    };

    internal static void Append(PreparationRun run)
    {
        run = Sanitize(run);
        if (!Valid(run)) throw new ArgumentException(Str.T("Str.PreparationHistoryUnreadable"));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var lease = new FileStream(Path.ChangeExtension(FilePath, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = Read(); // Refuse unreadable/future records; never replace them with an empty history.
        if (previous.Any(r => r.Id == run.Id)) throw new IOException(Str.T("Str.LibraryConflict"));
        var runs = previous.Append(run).OrderByDescending(r => r.StartedAt).Take(50).ToArray();
        AtomicFile.WriteAllTextDurable(FilePath, JsonSerializer.Serialize(new Document(1, runs), Json), new UTF8Encoding(false));
    }

    internal static void Export(PreparationRun run, string path)
    {
        if (!Valid(run)) throw new InvalidDataException(Str.T("Str.PreparationHistoryUnreadable"));
        var target = Path.GetFullPath(path);
        var root = Path.GetFullPath(UserDataPaths.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException(Str.T("Str.PreparationHistoryExportLocation"));
        AtomicFile.WriteAllTextDurable(target, JsonSerializer.Serialize(new Document(1, [Sanitize(run)]), Json), new UTF8Encoding(false));
    }
}
