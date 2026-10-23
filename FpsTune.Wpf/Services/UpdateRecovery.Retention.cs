using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FpsTune.Wpf.Services;

internal static partial class UpdateRecovery
{
    internal const int RetainedAttempts = 3;

    internal static FileStream AcquireCacheLease()
    {
        var path = OwnedPath("cache.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException(Str.T("Str.UpdateInProgress"), ex); }
    }

    internal static void PruneCache()
    {
        try { using var lease = AcquireCacheLease(); PruneCacheOwned(); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Caller owns cache.lock throughout download/preparation. Workers protect their receipts/packages with handles.
    internal static void PruneCacheOwned(params string[] pinnedPackages)
    {
        try { PruneCacheCore(pinnedPackages); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void PruneCacheCore(string[] pinnedPackages)
    {
        var recent = Recent();
        var attemptsRoot = OwnedPath("attempts");
        var attempts = new List<UpdateAttempt>();
        if (Directory.Exists(attemptsRoot))
            foreach (var directory in Directory.EnumerateDirectories(attemptsRoot))
            {
                var id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                var attempt = ReadAttempt(id);
                if (attempt is not null) attempts.Add(attempt);
                else if (!File.Exists(OwnedPath("attempts", id, "launch.json")) && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    DeleteOwnedFiles("attempts", id, AttemptFile);
            }

        var retained = attempts.OrderByDescending(a => a.CreatedAt).Take(RetainedAttempts).Select(a => a.Id).ToHashSet();
        if (recent is not null) retained.Add(recent.Id);
        foreach (var attempt in attempts.Where(a => Busy(a) || ProcessMatches(a.WorkerId, a.WorkerStartedTicks)))
            retained.Add(attempt.Id);

        var protectedAnchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in attempts.Where(a => retained.Contains(a.Id)))
            if (RecoveryAnchors(attempt) is { } anchors) protectedAnchors.UnionWith(anchors);
        var packages = pinnedPackages.ToHashSet(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            if (!retained.Contains(attempt.Id))
            {
                // Unique restoration evidence is data, not disposable cache. Keep its receipt/copy,
                // while obsolete installers can still expire; only recent attempts expose rollback.
                if (RecoveryAnchors(attempt) is { } anchors && anchors.All(protectedAnchors.Contains))
                    DeleteOwnedFiles("attempts", attempt.Id, AttemptFile);
                continue;
            }
            packages.Add(attempt.PackageId);
            if (attempt.PreviousPackageId is not null) packages.Add(attempt.PreviousPackageId);
        }
        var packagesRoot = OwnedPath("packages");
        if (!Directory.Exists(packagesRoot)) return;
        var candidates = new List<(CachedUpdatePackage Package, DateTime Created)>();
        foreach (var directory in Directory.EnumerateDirectories(packagesRoot))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try { candidates.Add((LoadPackage(id), File.GetLastWriteTimeUtc(OwnedPath("packages", id, "package.json")))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                if (!File.Exists(OwnedPath("packages", id, "package.json")) && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    DeleteOwnedFiles("packages", id, PackageFile);
            }
        }
        // Keep at least two recent complete downloads, plus every retained attempt's upgrade/rollback packages.
        foreach (var candidate in candidates.OrderByDescending(c => c.Created))
        {
            if (packages.Count < 2) packages.Add(candidate.Package.Id);
            if (!packages.Contains(candidate.Package.Id)) DeleteOwnedFiles("packages", candidate.Package.Id, PackageFile);
        }
    }

    private static HashSet<string>? RecoveryAnchors(UpdateAttempt attempt)
    {
        try
        {
            var path = AttemptPath(attempt.Id, "settings-backups.zip");
            if (new FileInfo(path).Length > 260L * 1024 * 1024 || !UpdateService.VerifySha256(path, attempt.SnapshotHash)) return null;
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > 5000) return null;
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                var module = entry.FullName.Split('/')[0];
                if (module is not ("backup" or "display-quality" or "display-icc" or "screen-color" or "audio-equalizer")) continue;
                using var input = entry.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    total += count;
                    if (total > 256L * 1024 * 1024) return null;
                    hash.AppendData(buffer, 0, count);
                }
                hashes.Add(entry.FullName + "|" + Convert.ToHexString(hash.GetHashAndReset()));
            }
            return hashes;
        }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }

    internal static void DiscardUnpublishedAttempt(string id)
    {
        // Only a pre-receipt snapshot belongs to this failed preparation; never remove a published recovery point.
        if (!File.Exists(AttemptPath(id, "launch.json")))
            DeleteOwnedFiles("attempts", id, name => name == "settings-backups.zip");
    }

    private static bool AttemptFile(string name) => name is "launch.json" or "outcome.json" or "settings-backups.zip"
        or "worker.ps1" or "ready" or "abort" or "worker-error.txt" or "installer.log"
        || Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _) && Path.GetExtension(name) == ".tmp";
    private static bool PackageFile(string name) => name is "package.json" or "sha256.txt"
        || name.StartsWith("FpsTune-Setup-", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.Ordinal)
        && UpdateService.TryNormalizeVersion(name[14..^4], out _);

    private static bool DeleteOwnedFiles(string category, string id, Func<string, bool> allowed)
    {
        var leases = new List<FileStream>();
        try
        {
            ValidId(id);
            var directory = OwnedPath(category, id);
            if (!Directory.Exists(directory)) return true;
            var entries = Directory.GetFileSystemEntries(directory);
            if (entries.Any(e => !File.Exists(e) || !allowed(Path.GetFileName(e)))) return false;
            // Validate every path before any deletion, and hold leases so in-use files abort the whole cleanup.
            var paths = entries.Select(e => OwnedPath(category, id, Path.GetFileName(e))).ToArray();
            foreach (var path in paths) leases.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete));
            foreach (var path in paths) File.Delete(path);
            foreach (var lease in leases) lease.Dispose();
            Directory.Delete(OwnedPath(category, id), recursive: false);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}
