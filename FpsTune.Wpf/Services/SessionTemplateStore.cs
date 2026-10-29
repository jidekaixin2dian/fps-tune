using System.IO;
using System.Text;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>One atomic, path-hashed template per game. Never edits game or display settings.</summary>
internal static class SessionTemplateStore
{
    private sealed record Template(int SchemaVersion, SessionConditions Conditions);

    internal static string FileFor(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.Any(char.IsControl)) throw new ArgumentException(Str.T("Str.SessionTemplateSelectGame"));
        var key = SessionConditionRules.CaptureGame(path, null).GameKey!;
        return Path.Combine(UserDataPaths.Root, "session-templates", key + ".json");
    }

    internal static SessionConditions? Load(string path)
    {
        var file = FileFor(path);
        if (!File.Exists(file)) return null;
        try
        {
            if (new FileInfo(file).Length > 32768) throw new InvalidDataException();
            var template = JsonSerializer.Deserialize<Template>(File.ReadAllText(file, Encoding.UTF8));
            if (template is not { SchemaVersion: 1, Conditions: { } c } || !SessionConditionRules.IsValid(c)
                || c.GameKey != SessionConditionRules.CaptureGame(path, null).GameKey) throw new InvalidDataException();
            return c;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new InvalidDataException(Str.T("Str.SessionTemplateUnreadable"), ex); }
    }

    internal static void Save(string path, SessionConditions conditions)
    {
        var file = FileFor(path);
        var captured = SessionConditionRules.CaptureGame(path, conditions);
        if (!SessionConditionRules.IsValid(captured)) throw new ArgumentException(Str.T("Str.ConditionInvalid"));
        // Refuse to overwrite an unreadable/future template, keeping recovery data intact.
        _ = Load(path);
        AtomicFile.WriteAllTextDurable(file, JsonSerializer.Serialize(new Template(1, captured)), new UTF8Encoding(false));
    }

    internal static void Delete(string path) => File.Delete(FileFor(path));
}
