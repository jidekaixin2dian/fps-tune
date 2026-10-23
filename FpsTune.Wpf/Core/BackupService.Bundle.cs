using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public static partial class BackupService
{
    private const string BundleManifestName = "fpstune-backup-manifest.json";
    private const long BundleLimit = 100L * 1024 * 1024;
    private sealed record BundleFile(string Name, long Length, string Sha256);
    private sealed record BundleManifest(int SchemaVersion, string Machine, string Origin, List<BundleFile> Files);
    internal static Func<string>? TransferMachineOverride;

    private static string TransferMachine()
    {
        if (TransferMachineOverride is not null) return TransferMachineOverride();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        using var identity = WindowsIdentity.GetCurrent();
        var guid = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(guid) || identity.User is null) throw new InvalidDataException(Str.T("Str.BundleIdentityUnavailable"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guid + "|" + identity.User.Value)));
    }

    private static Dictionary<string, string> BundleRoots()
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal) { ["backup"] = BackupDir };
        // Legacy tests that redirect only the core backup directory must never touch real module data.
        if (BackupDirOverride is null || UserDataPaths.RootOverride is not null)
            foreach (var module in new[] { "display-quality", "display-icc", "screen-color", "audio-equalizer" })
                roots.Add(module, Path.Combine(UserDataPaths.Root, module));
        return roots;
    }

    private static void RequireOrdinaryPath(string path)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(Str.T("Str.BundleLinkedPath"));
    }

    private static bool SafeParts(string name) => name.Length <= 512 && !name.Contains('\\')
        && name.Split('/').All(p => p.Length > 0 && p is not "." and not ".." && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    private static (string Module, string Relative) BundleName(string name)
    {
        if (!SafeParts(name)) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        var split = name.IndexOf('/');
        if (split < 0 && IsBackupTransferName(name)) return ("backup", name); // Preserve the old ZIP layout for system files.
        if (split < 0) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        return (name[..split], name[(split + 1)..]);
    }

    private static bool IsBundleCandidate(string module, string relative)
    {
        if (!SafeParts(relative)) return false;
        var parts = relative.Split('/'); var name = parts[^1];
        if (parts[0] == "history") return parts.Length is 2 or 3 && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (module == "display-icc" && parts.Length == 2 && parts[0] is "custom" or "presets")
            return (name.StartsWith("FpsTune-", StringComparison.Ordinal) && Path.GetExtension(name).ToLowerInvariant() is ".icc" or ".icm");
        if (parts.Length != 1) return false;
        return module switch
        {
            "backup" => IsBackupTransferName(name) || name == RestoreJournalFileName,
            "display-quality" => name.StartsWith("backup-", StringComparison.Ordinal) && name.EndsWith(".exe.json", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("vibrance", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal),
            "display-icc" => name.StartsWith("backup", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal),
            "screen-color" => name.StartsWith("color-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal),
            "audio-equalizer" => name == "receipt.json",
            _ => false
        };
    }

    private static void ValidateBundleFile(string module, string relative, byte[] bytes, bool importing)
    {
        if (!IsBundleCandidate(module, relative) || bytes.Length > (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? 10 * 1024 * 1024 : 16 * 1024 * 1024))
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        if (module == "display-icc" && relative.Split('/')[0] is "custom" or "presets")
        {
            if (bytes.Length < 128 || Encoding.ASCII.GetString(bytes, 12, 8) != "mntrRGB " || !new MscmsIccApi().IsProfileBytesValid(bytes))
                throw new InvalidDataException(Str.T("Str.IccFileNotDisplay"));
            return;
        }
        using var parsed = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 64 });
        if (relative.StartsWith("history/", StringComparison.Ordinal)) return; // Inert audit copies; never become active receipts.
        var name = Path.GetFileName(relative);
        switch (module)
        {
            case "backup":
                if (name == RestoreJournalFileName)
                {
                    var r = JsonSerializer.Deserialize<RestoreInFlight>(bytes);
                    if (r is null || !IsSafeBackupFileName(r.BackupFile) || !IsSafeRecordId(r.RecordId) || r.RecordIndex < 0 || !IsSha256(r.RecordFingerprint))
                        throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
                }
                else foreach (var record in DecodeRecords(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))) ValidateRecord(record);
                break;
            case "display-quality":
                if (name.StartsWith("vibrance", StringComparison.Ordinal)) DigitalVibranceService.ValidateTransferredBackup(name, bytes);
                else DisplayQualityService.ValidateTransferredBackup(name, bytes);
                break;
            case "display-icc": IccFilterService.ValidateTransferredBackup(name, bytes); break;
            case "screen-color": ScreenColorService.ValidateTransferredBackup(name, bytes); break;
            case "audio-equalizer": AudioEqualizerService.ValidateTransferredBackup(bytes, importing); break;
        }
    }

    private static int ExportBundle(string zipPath)
    {
        using var gate = SystemMutationGate.Acquire();
        var roots = BundleRoots(); var files = new List<(BundleFile Info, byte[] Bytes)>(); long total = 0;
        var origin = GetOriginId();
        foreach (var (module, root) in roots)
        {
            if (!Directory.Exists(root)) continue;
            RequireOrdinaryPath(root);
            var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
            while (pending.TryPop(out var dir))
            {
                RequireOrdinaryPath(dir.Path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir.Path))
                {
                    RequireOrdinaryPath(entry);
                    if (Directory.Exists(entry)) { if (dir.Depth < 2) pending.Push((entry, dir.Depth + 1)); continue; }
                    var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    if (!IsBundleCandidate(module, relative)) continue;
                    if (module == "backup" && !relative.Contains('/') && IsBackupTransferName(relative) && !IsCSharpBackupFile(entry)) continue;
                    if (new FileInfo(entry).Length > 16 * 1024 * 1024 || files.Count >= 999) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
                    var bytes = File.ReadAllBytes(entry); total += bytes.Length;
                    if (total > BundleLimit) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
                    ValidateBundleFile(module, relative, bytes, false);
                    if (module == "backup" && Path.GetFileName(relative).StartsWith(V2BackupPrefix, StringComparison.Ordinal) && !relative.Contains('/'))
                        _ = ReadRecords(entry, requireLocalOrigin: true);
                    var name = module == "backup" && !relative.Contains('/') && IsBackupTransferName(relative) ? relative : module + "/" + relative;
                    files.Add((new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))), bytes));
                }
            }
        }
        var manifest = new BundleManifest(1, TransferMachine(), origin, files.Select(f => f.Info).ToList());
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (manifestBytes.Length > 1024 * 1024 || total + manifestBytes.Length > BundleLimit)
            throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
        var target = Path.GetFullPath(zipPath);
        if (roots.Any(r => files.Any(f => Path.GetFullPath(Path.Combine(r.Value, BundleName(f.Info.Name).Relative)).Equals(target, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    foreach (var file in files) { using var output = zip.CreateEntry(file.Info.Name).Open(); output.Write(file.Bytes); }
                    using var metadata = zip.CreateEntry(BundleManifestName).Open();
                    metadata.Write(manifestBytes);
                }
                stream.Flush(true);
            }
            DiagnosticReportExporter.MoveArchiveAtomically(temporary, target);
            return files.Count;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static (int Imported, int SkippedDuplicate, int SkippedInvalid) ImportBundle(ZipArchive zip)
    {
        var metadata = zip.GetEntry(BundleManifestName)!;
        if (metadata.Length > 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
        using var manifestStream = metadata.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream) ?? throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        if (manifest.SchemaVersion != 1 || !IsSha256(manifest.Machine) || manifest.Machine != TransferMachine()) throw new InvalidDataException(Str.T("Str.BundleOtherMachine"));
        if (!Guid.TryParse(manifest.Origin, out _) || manifest.Files is null || manifest.Files.Count > 999
            || manifest.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count
            || zip.Entries.Count != manifest.Files.Count + 1 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        var roots = BundleRoots(); var staged = new List<(string Target, byte[] Bytes, byte[]? Original)>(); long total = 0; var duplicates = 0;
        foreach (var file in manifest.Files)
        {
            var (module, relative) = BundleName(file.Name);
            if (!roots.TryGetValue(module, out var root) || file.Length < 0 || file.Length > 16 * 1024 * 1024 || !IsSha256(file.Sha256))
                throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
            var entry = zip.GetEntry(file.Name) ?? throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
            if (entry.Length != file.Length || (total += file.Length) > BundleLimit) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
            using var input = entry.Open(); using var memory = new MemoryStream();
            var chunk = new byte[81920]; int read;
            while ((read = input.Read(chunk)) > 0) { if (memory.Length + read > file.Length) throw new InvalidDataException(Str.T("Str.BackupImportLimit")); memory.Write(chunk, 0, read); }
            var bytes = memory.ToArray();
            if (bytes.LongLength != file.Length || Convert.ToHexString(SHA256.HashData(bytes)) != file.Sha256)
                throw new InvalidDataException(Str.T("Str.BundleHashMismatch"));
            ValidateBundleFile(module, relative, bytes, true);
            byte[]? original = null;
            if (module == "backup" && relative.StartsWith(V2BackupPrefix, StringComparison.Ordinal) && !relative.Contains('/'))
            {
                var document = DecodeDocument(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                if (document.OriginId != manifest.Origin) throw new InvalidDataException(Str.T("Str.BackupOtherOrigin"));
                var localOrigin = GetOriginId();
                if (document.OriginId != localOrigin)
                {
                    original = bytes; document.ImportedOriginId = document.OriginId; document.OriginId = localOrigin;
                    bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, BackupJsonOptions));
                }
            }
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
            RequireOrdinaryPath(target);
            if (File.Exists(target))
            {
                if (new FileInfo(target).Length != bytes.Length || !File.ReadAllBytes(target).SequenceEqual(bytes)) throw new IOException(Str.T("Str.BundleConflict", file.Name));
                duplicates++; continue;
            }
            staged.Add((target, bytes, original));
        }
        // Publish complete validated files only; a retry can safely skip files from an interrupted import.
        foreach (var file in staged.OrderBy(f => Path.GetFileName(f.Target) == RestoreJournalFileName))
        {
            RequireOrdinaryPath(file.Target);
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            RequireOrdinaryPath(file.Target);
            if (file.Original is not null)
            {
                var saved = Path.Combine(BackupDir, "history", "import-" + Convert.ToHexString(SHA256.HashData(file.Original)) + ".json");
                RequireOrdinaryPath(saved);
                if (!File.Exists(saved)) PublishImportedFile(saved, file.Original);
            }
            PublishImportedFile(file.Target, file.Bytes);
        }
        return (staged.Count, duplicates, 0);
    }

    private static void PublishImportedFile(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        RequireOrdinaryPath(target);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, target); // Never overwrite a local receipt, even if another writer raced the preflight.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
