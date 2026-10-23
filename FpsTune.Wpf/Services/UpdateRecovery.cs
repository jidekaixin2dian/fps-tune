using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

internal sealed record CachedUpdatePackage(string Id, string Version, string Tag, string Hash);
internal sealed record UpdateAttempt(string Id, string FromVersion, UpdateInfo Target, string? PreviousPackageId,
    string PackageId, string InstallDirectory, int ParentId, long ParentStartedTicks, string SnapshotHash,
    DateTimeOffset CreatedAt, string PackageHash, string Mode = "upgrade", string Status = "prepared", string Stage = "prepare",
    int? ExitCode = null, string? Error = null, int? WorkerId = null, long? WorkerStartedTicks = null);
internal sealed record UpdateOutcome(string Id, string Status, string Stage, int? ExitCode = null,
    string? Error = null, int? WorkerId = null, long? WorkerStartedTicks = null);

/// <summary>Immutable launch receipts, verified package cache and local protection copies. Never restores data automatically.</summary>
internal static partial class UpdateRecovery
{
    internal static string Root => Path.Combine(UserDataPaths.Root, "updates");
    internal static Func<UpdateInfo, Action<double>?, CancellationToken, Task<CachedUpdatePackage>>? PackageOverride;
    internal static Func<string, CancellationToken, Task<UpdateInfo?>>? ReleaseOverride;
    internal static Func<UpdateAttempt, CancellationToken, Task>? WorkerOverride;
    internal static string? InstallDirectoryOverride;
    internal static string? CurrentVersionOverride;
    internal static string CurrentVersion => CurrentVersionOverride ?? UpdateService.DisplayVersion;
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static string OwnedPath(params string[] parts)
    {
        var root = Path.GetFullPath(Root);
        RequireSnapshotPath(root);
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update path is outside its cache.");
        var current = root;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Update cache must not be a filesystem link.");
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update cache must not contain filesystem links.");
        }
        return path;
    }
    private static void ValidId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid update record identity.");
    }
    internal static string AttemptPath(string id, string name)
    {
        ValidId(id);
        return OwnedPath("attempts", id, name);
    }
    internal static string PackagePath(CachedUpdatePackage package)
    {
        ValidatePackage(package);
        return OwnedPath("packages", package.Id, UpdateService.InstallerAssetName(package.Version));
    }
    private static void ValidatePackage(CachedUpdatePackage package)
    {
        ValidId(package.Id);
        if (!UpdateService.TryNormalizeVersion(package.Version, out var number) || number != package.Version
            || Version.Parse(number).Major == 1 || !UpdateService.TryNormalizeVersion(package.Tag, out var tagNumber)
            || tagNumber != number || package.Hash is null || package.Hash.Length != 64 || !package.Hash.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid cached update package.");
    }
    internal static void ValidateInfo(UpdateInfo info)
    {
        if (!UpdateService.TryNormalizeVersion(info.Version, out var version) || version != info.Version
            || Version.Parse(version).Major == 1 || info.ReleaseTag is null
            || !UpdateService.TryNormalizeVersion(info.ReleaseTag, out var tagVersion) || tagVersion != version)
            throw new InvalidDataException(Str.T("Str.UpdateUrlInvalid"));
        var prefix = "https://github.com/jidekaixin2dian/fps-tune/releases/";
        if (info.Url != prefix + "tag/" + info.ReleaseTag
            || info.InstallerUrl != prefix + "download/" + info.ReleaseTag + "/" + UpdateService.InstallerAssetName(version)
            || info.ChecksumUrl != prefix + "download/" + info.ReleaseTag + "/" + UpdateService.ChecksumAssetName(version))
            throw new InvalidDataException(Str.T("Str.UpdateUrlInvalid"));
    }

    internal static async Task<CachedUpdatePackage> GetPackageAsync(UpdateInfo info, Action<double>? progress, CancellationToken token)
    {
        ValidateInfo(info);
        if (PackageOverride is not null) return await PackageOverride(info, progress, token);
        var id = Guid.NewGuid().ToString("N");
        var directory = OwnedPath("packages", id);
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "sha256.txt");
        var installer = Path.Combine(directory, UpdateService.InstallerAssetName(info.Version));
        var ready = false;
        try
        {
            // Always fetch the official manifest again before executing a cached package.
            await UpdateService.DownloadAsync(info.ChecksumUrl!, manifest, null, token, 1024 * 1024);
            if (!UpdateService.TryReadSha256(await File.ReadAllTextAsync(manifest, token), Path.GetFileName(installer), out var hash))
                throw new InvalidDataException(Str.T("Str.ManifestMissingEntry"));
            var cached = FindPackage(info.ReleaseTag!, hash);
            if (cached is not null) { ready = false; progress?.Invoke(100); return cached; }
            await UpdateService.DownloadAsync(info.InstallerUrl!, installer, progress, token);
            if (!await Task.Run(() => UpdateService.VerifySha256(installer, hash), token))
                throw new InvalidDataException(Str.T("Str.InstallerHashMismatch"));
            var package = new CachedUpdatePackage(id, info.Version, info.ReleaseTag!, hash);
            AtomicFile.WriteAllTextDurable(Path.Combine(directory, "package.json"), JsonSerializer.Serialize(package), Utf8);
            ready = true;
            return package;
        }
        finally
        {
            if (!ready)
            {
                try { Directory.Delete(OwnedPath("packages", id), true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    private static CachedUpdatePackage? FindPackage(string tag, string hash)
    {
        var directory = OwnedPath("packages");
        if (!Directory.Exists(directory)) return null;
        foreach (var folder in Directory.GetDirectories(directory).OrderDescending().Take(100))
        {
            try
            {
                var id = Path.GetFileName(folder);
                var package = LoadPackage(id);
                if (package.Tag == tag && package.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)
                    && UpdateService.VerifySha256(PackagePath(package), hash)) return package;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (JsonException) { }
        }
        return null;
    }
    internal static CachedUpdatePackage LoadPackage(string id)
    {
        ValidId(id);
        var path = OwnedPath("packages", id, "package.json");
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Update metadata is too large.");
        var package = JsonSerializer.Deserialize<CachedUpdatePackage>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing update metadata.");
        ValidatePackage(package);
        if (package.Id != id) throw new InvalidDataException("Update package identity mismatch.");
        return package;
    }

    internal static string Snapshot(string id)
    {
        var destination = AttemptPath(id, "settings-backups.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var source = Path.GetFullPath(UserDataPaths.Root);
        long total = 0;
        var files = 0;
        var created = false;
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                created = true;
                // Installation preserves all user data in place. Only mutable settings and recovery anchors
                // need an extra protection copy; recordings, logs, wallpapers and caches are never removed.
                RequireSnapshotPath(source);
                var queue = new Queue<string>();
                foreach (var module in new[] { "backup", "display-quality", "display-icc", "screen-color", "audio-equalizer" })
                {
                    var directory = Path.Combine(source, module);
                    if (Directory.Exists(directory)) queue.Enqueue(directory);
                }
                foreach (var name in new[] { "settings.json", "profiles.json", "games.json", "game-path.txt", "lang.txt", "onboarding.done",
                    "experiment/state.json", "experiment/state-v2.json", "experiment/in-flight-v2.json", "experiment/wizard.json" })
                {
                    var file = Path.Combine(source, name);
                    if (File.Exists(file)) Copy(file);
                }
                while (queue.TryDequeue(out var directory))
                {
                    RequireSnapshotPath(directory);
                    foreach (var child in Directory.EnumerateDirectories(directory).Order())
                        queue.Enqueue(child);
                    foreach (var file in Directory.EnumerateFiles(directory).Order())
                        Copy(file);
                }
                void Copy(string file)
                {
                    RequireSnapshotPath(file);
                    if (++files > 5000) throw new IOException(Str.T("Str.UpdateSnapshotLarge"));
                    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var entry = zip.CreateEntry(relative, CompressionLevel.Fastest).Open();
                    var buffer = new byte[81920];
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        total += count;
                        if (total > 256L * 1024 * 1024) throw new IOException(Str.T("Str.UpdateSnapshotLarge"));
                        entry.Write(buffer, 0, count);
                    }
                }
            }
            using var saved = File.OpenRead(destination);
            return Convert.ToHexString(SHA256.HashData(saved));
        }
        catch
        {
            if (created) DiscardUnpublishedAttempt(id);
            throw;
        }
    }
    private static void RequireSnapshotPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(Str.T("Str.UpdateSnapshotLink"));
    }
    internal static string? ProtectedCopy(UpdateAttempt attempt)
    {
        var path = AttemptPath(attempt.Id, "settings-backups.zip");
        return UpdateService.VerifySha256(path, attempt.SnapshotHash) ? path : null;
    }
    internal static void SaveAttempt(UpdateAttempt attempt)
    {
        AtomicFile.WriteAllTextDurable(AttemptPath(attempt.Id, "launch.json"), JsonSerializer.Serialize(attempt), Utf8);
        AtomicFile.WriteAllTextDurable(OwnedPath("last-attempt.txt"), attempt.Id, Utf8);
    }
    internal static void SaveOutcome(UpdateOutcome outcome)
        => AtomicFile.WriteAllTextDurable(AttemptPath(outcome.Id, "outcome.json"), JsonSerializer.Serialize(outcome), Utf8);

    internal static UpdateAttempt? Recent()
    {
        try
        {
            var pointer = OwnedPath("last-attempt.txt");
            if (!File.Exists(pointer) || new FileInfo(pointer).Length > 64) return null;
            var id = File.ReadAllText(pointer).Trim();
            return ReadAttempt(id);
        }
        catch { return null; }
    }
    private static UpdateAttempt? ReadAttempt(string id)
    {
        try
        {
            var path = AttemptPath(id, "launch.json");
            if (new FileInfo(path).Length > 64 * 1024) return null;
            var attempt = JsonSerializer.Deserialize<UpdateAttempt>(File.ReadAllText(path));
            if (attempt is null || attempt.Id != id || attempt.Target is null || attempt.Mode is not ("upgrade" or "rollback")
                || !UpdateService.TryNormalizeVersion(attempt.FromVersion, out _) || attempt.Status != "prepared"
                || attempt.Stage != "prepare" || attempt.Error is not null || attempt.ExitCode is not null) return null;
            ValidateInfo(attempt.Target);
            ValidId(attempt.PackageId);
            if (attempt.PreviousPackageId is not null) ValidId(attempt.PreviousPackageId);
            if (!Path.IsPathFullyQualified(attempt.InstallDirectory) || !ValidHash(attempt.SnapshotHash)
                || !ValidHash(attempt.PackageHash)) return null;
            var outcomePath = AttemptPath(id, "outcome.json");
            if (!File.Exists(outcomePath)) return attempt;
            if (new FileInfo(outcomePath).Length > 16 * 1024) return null;
            using var stream = new FileStream(outcomePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var outcome = JsonSerializer.Deserialize<UpdateOutcome>(stream);
            if (outcome is null || outcome.Id != id || outcome.Status is not ("waiting-for-exit" or "installing" or "awaiting-startup"
                or "succeeded" or "failed" or "cancelled" or "interrupted") || outcome.Stage is not ("prepare" or "verify-package"
                or "wait-for-exit" or "operation-lock" or "run-installer" or "installer-exit" or "verify-installed-file" or "verify-startup" or "handoff")) return null;
            if (outcome.Status == "succeeded" && (outcome.ExitCode != 0 || outcome.Stage is not ("verify-startup" or "verify-installed-file"))) return null;
            return attempt with { Status = outcome.Status, Stage = outcome.Stage, ExitCode = outcome.ExitCode,
                Error = OperationDiagnostics.Scrub(outcome.Error), WorkerId = outcome.WorkerId, WorkerStartedTicks = outcome.WorkerStartedTicks };
        }
        catch { return null; }
    }
    internal static bool ProcessMatches(int? id, long? started)
    {
        if (id is null || started is null) return false;
        try { using var process = Process.GetProcessById(id.Value); return process.StartTime.ToUniversalTime().Ticks == started && !process.HasExited; }
        catch { return false; }
    }
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    internal static bool Busy(UpdateAttempt attempt) => (attempt.Status is "prepared" or "waiting-for-exit" or "installing")
        && (ProcessMatches(attempt.WorkerId, attempt.WorkerStartedTicks) || ProcessMatches(attempt.ParentId, attempt.ParentStartedTicks));

    internal static UpdateAttempt? ResolveStartup(string version, string executable)
    {
        var attempt = Recent();
        if (attempt is null) { PruneCache(); return null; }
        if (attempt.Status == "awaiting-startup")
        {
            var sameVersion = UpdateService.TryNormalizeVersion(version, out _) && !UpdateService.IsNewer(version, attempt.Target.ReleaseTag!)
                && !UpdateService.IsNewer(attempt.Target.ReleaseTag!, version);
            var sameLocation = Path.GetFullPath(executable).Equals(Path.Combine(attempt.InstallDirectory, "FpsTune.exe"), StringComparison.OrdinalIgnoreCase);
            var status = sameVersion && sameLocation && attempt.ExitCode == 0 ? "succeeded" : "failed";
            SaveOutcome(new(attempt.Id, status, "verify-startup", attempt.ExitCode,
                status == "failed" ? Str.T("Str.UpdateVersionMismatch") : null));
        }
        else if ((attempt.Status is "prepared" or "waiting-for-exit" or "installing") && !Busy(attempt))
            SaveOutcome(new(attempt.Id, "interrupted", attempt.Stage, attempt.ExitCode, Str.T("Str.UpdateInterrupted")));
        PruneCache();
        return Recent();
    }
    internal static object DiagnosticSummary(string? root = null)
    {
        try { return DiagnosticSummaryCore(root); }
        catch { return new { status = "unavailable" }; }
    }
    internal static bool CanRollback(UpdateAttempt attempt)
    {
        try { return attempt.PreviousPackageId is not null && File.Exists(PackagePath(LoadPackage(attempt.PreviousPackageId))); }
        catch { return false; }
    }
    private static object DiagnosticSummaryCore(string? root)
    {
        // Export metadata only; never include packages, protection ZIPs, absolute paths or arbitrary launch data.
        if (root is not null && !Path.GetFullPath(root).Equals(Path.GetFullPath(UserDataPaths.Root), StringComparison.OrdinalIgnoreCase))
            return new { status = "separate-data-root" };
        var attempt = Recent();
        return attempt is null ? new { status = "no-records" } : (object)new
        {
            attempt.Id, attempt.CreatedAt, attempt.FromVersion, targetVersion = attempt.Target.ReleaseTag,
            attempt.Mode, attempt.Status, attempt.Stage, attempt.ExitCode, attempt.Error,
            protectedCopyAvailable = ProtectedCopy(attempt) is not null,
            previousPackageAvailable = CanRollback(attempt)
        };
    }
}
