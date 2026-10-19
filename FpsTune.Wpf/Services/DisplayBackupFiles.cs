using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FpsTune.Wpf.Services;

internal sealed record DisplayBackupTarget(string? Identity, string Name, string? Error = null);
internal static class DisplayBackupFiles
{
    internal static void Archive(string path) => AtomicFile.WriteAllTextDurable(
        Path.Combine(Path.GetDirectoryName(path)!, "history", $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.json"),
        File.ReadAllText(path), new UTF8Encoding(false));
    internal static string PathFor(string directory, string prefix, string? identity)
        => Path.Combine(directory, identity is null ? prefix + ".json"
            : prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant()))) + ".json");
    internal static IEnumerable<string> List(string directory, string prefix)
    {
        if (!Directory.Exists(directory)) return [];
        var files = Directory.GetFiles(directory, prefix + "*.json").Where(p => Path.GetFileName(p) == prefix + ".json"
            || Path.GetFileNameWithoutExtension(p) is var name && name.Length == prefix.Length + 65
                && name.StartsWith(prefix + "-", StringComparison.Ordinal) && name[(prefix.Length + 1)..].All(char.IsAsciiHexDigit)).ToArray();
        if (files.Length > 128) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
        return files;
    }
}
