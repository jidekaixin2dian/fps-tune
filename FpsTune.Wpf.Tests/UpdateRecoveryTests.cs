using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class UpdateRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-update-' 中文 " + Guid.NewGuid().ToString("N"));
    public UpdateRecoveryTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        UpdateRecovery.CurrentVersionOverride = "0.2.7-beta";
        UpdateRecovery.InstallDirectoryOverride = Path.Combine(_root, "app");
        Directory.CreateDirectory(UpdateRecovery.InstallDirectoryOverride);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "original settings");
        Directory.CreateDirectory(Path.Combine(_root, "display-quality"));
        File.WriteAllText(Path.Combine(_root, "display-quality", "original.json"), "original restore receipt");
        UpdateRecovery.PackageOverride = (info, _, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(Package(info)); };
        UpdateRecovery.ReleaseOverride = (tag, _) => Task.FromResult<UpdateInfo?>(Info(tag));
        UpdateRecovery.WorkerOverride = (attempt, _) =>
        {
            UpdateRecovery.SaveOutcome(new(attempt.Id, "failed", "run-installer", 4));
            return Task.CompletedTask;
        };
    }
    public void Dispose()
    {
        UpdateRecovery.PackageOverride = null;
        UpdateRecovery.ReleaseOverride = null;
        UpdateRecovery.WorkerOverride = null;
        UpdateRecovery.CurrentVersionOverride = null;
        UpdateRecovery.InstallDirectoryOverride = null;
        UserDataPaths.RootOverride = null;
        DiagnosticReportExporter.BaseDirOverride = null;
        Directory.Delete(_root, true);
    }
    internal static UpdateInfo Info(string tag = "v0.2.8-beta")
    {
        tag = tag.StartsWith('v') ? tag : "v" + tag;
        Assert.True(UpdateService.TryNormalizeVersion(tag, out var version));
        const string prefix = "https://github.com/jidekaixin2dian/fps-tune/releases/";
        return new(version, prefix + "tag/" + tag, "notes", prefix + "download/" + tag + "/" + UpdateService.InstallerAssetName(version),
            prefix + "download/" + tag + "/" + UpdateService.ChecksumAssetName(version), tag);
    }
    private CachedUpdatePackage Package(UpdateInfo info, byte[]? content = null)
    {
        var package = new CachedUpdatePackage(Guid.NewGuid().ToString("N"), info.Version, info.ReleaseTag!,
            Convert.ToHexString(SHA256.HashData(content ?? [1, 2, 3, 4])));
        var path = UpdateRecovery.PackagePath(package);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4]);
        File.WriteAllText(UpdateRecovery.OwnedPath("packages", package.Id, "package.json"), JsonSerializer.Serialize(package));
        return package;
    }
    private UpdateAttempt Attempt(CachedUpdatePackage? package = null, Process? parent = null, string mode = "upgrade")
    {
        package ??= Package(Info());
        var id = Guid.NewGuid().ToString("N");
        var snapshotHash = UpdateRecovery.Snapshot(id);
        var attempt = new UpdateAttempt(id, "0.2.7-beta", Info(package.Tag), null, package.Id, UpdateRecovery.InstallDirectoryOverride!,
            parent?.Id ?? -1, parent?.StartTime.ToUniversalTime().Ticks ?? 0, snapshotHash, DateTimeOffset.UtcNow, package.Hash, mode);
        UpdateRecovery.SaveAttempt(attempt);
        return attempt;
    }
    [Fact]
    public void Snapshot_excludes_large_recordings_and_cache_but_preserves_every_recovery_module()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        using (var large = File.Create(Path.Combine(_root, "sessions", "large.json"))) large.SetLength(300L * 1024 * 1024);
        foreach (var module in new[] { "backup", "display-icc", "screen-color", "audio-equalizer" })
        { Directory.CreateDirectory(Path.Combine(_root, module)); File.WriteAllText(Path.Combine(_root, module, "anchor.json"), "original"); }
        var id = Guid.NewGuid().ToString("N");
        UpdateRecovery.Snapshot(id);
        using var zip = ZipFile.OpenRead(UpdateRecovery.AttemptPath(id, "settings-backups.zip"));
        Assert.Equal(6, zip.Entries.Count);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("sessions/"));
        Assert.Equal(300L * 1024 * 1024, new FileInfo(Path.Combine(_root, "sessions", "large.json")).Length);
    }
    [Fact]
    public void Failed_snapshot_removes_its_partial_copy_and_preserves_existing_recovery_points()
    {
        var previous = Attempt(); var id = Guid.NewGuid().ToString("N");
        using var locked = new FileStream(Path.Combine(_root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Throws<IOException>(() => UpdateRecovery.Snapshot(id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(UpdateRecovery.AttemptPath(id, "settings-backups.zip"))));
        Assert.NotNull(UpdateRecovery.ProtectedCopy(previous)); Assert.Equal(previous.Id, UpdateRecovery.Recent()!.Id);
    }
    [Fact]
    public void Retention_bounds_completed_cache_keeps_recent_rollback_and_skips_files_in_use()
    {
        var previous = Package(Info("v0.2.7-beta"));
        var attempts = Enumerable.Range(0, 5).Select(_ =>
        {
            var attempt = Attempt() with { PreviousPackageId = previous.Id };
            UpdateRecovery.SaveAttempt(attempt); UpdateRecovery.SaveOutcome(new(attempt.Id, "failed", "installer-exit", 4));
            return attempt;
        }).ToArray();
        using (var lease = new FileStream(UpdateRecovery.AttemptPath(attempts[0].Id, "settings-backups.zip"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            UpdateRecovery.PruneCache(); Assert.NotNull(UpdateRecovery.ProtectedCopy(attempts[0]));
        }
        UpdateRecovery.PruneCache();
        Assert.Equal(UpdateRecovery.RetainedAttempts, Directory.GetDirectories(UpdateRecovery.OwnedPath("attempts")).Length);
        Assert.Equal(4, Directory.GetDirectories(UpdateRecovery.OwnedPath("packages")).Length);
        Assert.True(UpdateRecovery.CanRollback(UpdateRecovery.Recent()!));
        Assert.NotNull(UpdateRecovery.ProtectedCopy(attempts[^1]));
        Assert.Equal("original settings", File.ReadAllText(Path.Combine(_root, "settings.json")));
        using var cacheLease = UpdateRecovery.AcquireCacheLease();
        Assert.Throws<IOException>(() => UpdateRecovery.AcquireCacheLease());
    }
    [Fact]
    public void Expired_installer_is_removed_but_unique_restore_evidence_is_preserved()
    {
        var unique = Attempt(); UpdateRecovery.SaveOutcome(new(unique.Id, "failed", "installer-exit", 4));
        var oldInstaller = UpdateRecovery.PackagePath(UpdateRecovery.LoadPackage(unique.PackageId));
        File.WriteAllText(Path.Combine(_root, "display-quality", "original.json"), "a later different original");
        for (var i = 0; i < 4; i++)
        { var newer = Attempt(); UpdateRecovery.SaveOutcome(new(newer.Id, "failed", "installer-exit", 4)); }
        UpdateRecovery.PruneCache();
        Assert.NotNull(UpdateRecovery.ProtectedCopy(unique));
        Assert.False(File.Exists(oldInstaller));
    }
    [Fact]
    public async Task Failure_keeps_original_settings_receipts_verified_previous_package_and_complete_protection_copy()
    {
        await UpdateInstaller.InstallAsync(Info(), _ => { }, default);
        var attempt = UpdateRecovery.Recent()!;
        Assert.Equal("failed", attempt.Status);
        Assert.Equal(4, attempt.ExitCode);
        Assert.True(UpdateRecovery.CanRollback(attempt));
        Assert.Equal("0.2.7", UpdateRecovery.LoadPackage(attempt.PreviousPackageId!).Version);
        using var zip = ZipFile.OpenRead(UpdateRecovery.ProtectedCopy(attempt)!);
        Assert.Contains(zip.Entries, e => e.FullName == "settings.json");
        Assert.Contains(zip.Entries, e => e.FullName == "display-quality/original.json");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("updates/"));
        Assert.Equal("original settings", File.ReadAllText(Path.Combine(_root, "settings.json")));
        Assert.Equal("original restore receipt", File.ReadAllText(Path.Combine(_root, "display-quality", "original.json")));
    }
    [Fact]
    public async Task Retry_rechecks_release_and_package_keeps_old_attempt_and_never_extracts_stale_protection_copy()
    {
        await UpdateInstaller.InstallAsync(Info(), _ => { }, default);
        var first = UpdateRecovery.Recent()!;
        File.WriteAllText(Path.Combine(_root, "settings.json"), "new settings after failure");
        string? queried = null;
        UpdateRecovery.ReleaseOverride = (tag, _) => { queried = tag; return Task.FromResult<UpdateInfo?>(Info(tag)); };
        await UpdateInstaller.RetryAsync(first, _ => { }, default);
        var retry = UpdateRecovery.Recent()!;
        Assert.Equal(first.Target.ReleaseTag, queried);
        Assert.NotEqual(first.Id, retry.Id);
        Assert.Equal(first.PreviousPackageId, retry.PreviousPackageId);
        Assert.True(File.Exists(UpdateRecovery.AttemptPath(first.Id, "launch.json")));
        Assert.NotNull(UpdateRecovery.ProtectedCopy(first));
        Assert.Equal("new settings after failure", File.ReadAllText(Path.Combine(_root, "settings.json")));
    }
    [Fact]
    public async Task Rollback_uses_previous_official_release_and_new_protection_copy_without_overwriting_settings()
    {
        await UpdateInstaller.InstallAsync(Info(), _ => { }, default);
        var failed = UpdateRecovery.Recent()!;
        var oldHash = failed.SnapshotHash;
        File.WriteAllText(Path.Combine(_root, "settings.json"), "current settings");
        await UpdateInstaller.RollbackAsync(failed, _ => { }, default);
        var rollback = UpdateRecovery.Recent()!;
        Assert.Equal("rollback", rollback.Mode);
        Assert.Equal("v0.2.7-beta", rollback.Target.ReleaseTag);
        Assert.NotEqual(oldHash, rollback.SnapshotHash);
        Assert.Equal("current settings", File.ReadAllText(Path.Combine(_root, "settings.json")));
        Assert.NotNull(UpdateRecovery.ProtectedCopy(failed));
    }
    [Fact]
    public async Task Missing_previous_release_does_not_block_upgrade_or_offer_unusable_rollback()
    {
        UpdateRecovery.ReleaseOverride = (_, _) => Task.FromResult<UpdateInfo?>(null);
        await UpdateInstaller.InstallAsync(Info(), _ => { }, default);
        Assert.False(UpdateRecovery.CanRollback(UpdateRecovery.Recent()!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.RollbackAsync(UpdateRecovery.Recent()!, _ => { }, default));
    }
    [Fact]
    public async Task Corrupt_package_or_failed_handover_never_becomes_success_and_is_retryable()
    {
        var package = Package(Info());
        File.AppendAllText(UpdateRecovery.PackagePath(package), "corrupted");
        UpdateRecovery.PackageOverride = (_, _, _) => Task.FromResult(package);
        var starts = 0;
        UpdateRecovery.WorkerOverride = (_, _) => { starts++; return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.InstallAsync(Info(), _ => { }, default));
        Assert.Equal(0, starts);
        Assert.Null(UpdateRecovery.Recent());
        UpdateRecovery.PackageOverride = (info, _, _) => Task.FromResult(Package(info));
        UpdateRecovery.WorkerOverride = (_, _) => throw new IOException("worker failed: password=should-not-leak");
        await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.InstallAsync(Info(), _ => { }, default));
        Assert.Equal("failed", UpdateRecovery.Recent()!.Status);
        Assert.DoesNotContain("should-not-leak", UpdateRecovery.Recent()!.Error);
        Assert.NotNull(UpdateRecovery.ProtectedCopy(UpdateRecovery.Recent()!));
    }
    [Theory]
    [InlineData("0.2.8-beta", true, "succeeded")]
    [InlineData("0.2.7-beta", true, "failed")]
    [InlineData("0.2.8-beta", false, "failed")]
    [InlineData("invalid", true, "failed")]
    public void Exit_zero_only_counts_as_success_after_matching_version_and_location(string version, bool correctLocation, string status)
    {
        var attempt = Attempt();
        UpdateRecovery.SaveOutcome(new(attempt.Id, "awaiting-startup", "installer-exit", 0));
        var executable = Path.Combine(attempt.InstallDirectory, correctLocation ? "FpsTune.exe" : "wrong.exe");
        Assert.Equal(status, UpdateRecovery.ResolveStartup(version, executable)!.Status);
    }
    [Fact]
    public void Dead_parent_and_worker_is_interruption_not_success_and_pid_reuse_does_not_count_as_alive()
    {
        using var current = Process.GetCurrentProcess();
        var attempt = Attempt(parent: current);
        UpdateRecovery.SaveOutcome(new(attempt.Id, "waiting-for-exit", "wait-for-exit", WorkerId: current.Id, WorkerStartedTicks: 1));
        Assert.True(UpdateRecovery.Busy(UpdateRecovery.Recent()!));
        Assert.False(UpdateRecovery.ProcessMatches(current.Id, 1));
        UpdateRecovery.SaveAttempt(attempt with { ParentId = -1, ParentStartedTicks = 0 });
        Assert.Equal("interrupted", UpdateRecovery.ResolveStartup("0.2.7-beta", Path.Combine(attempt.InstallDirectory, "FpsTune.exe"))!.Status);
    }
    [Fact]
    public void Diagnostic_projection_never_copies_private_protection_archive_or_raw_launch_paths()
    {
        var attempt = Attempt();
        UpdateRecovery.SaveOutcome(new(attempt.Id, "failed", "installer-exit", 4, @"password=do-not-share C:\Users\Private\secret"));
        var json = JsonSerializer.Serialize(UpdateRecovery.DiagnosticSummary());
        Assert.DoesNotContain(_root, json);
        Assert.DoesNotContain("do-not-share", json);
        Assert.DoesNotContain("Private", json);
        Assert.Contains("installer-exit", json);
        Assert.Contains("protectedCopyAvailable", json);
        DiagnosticReportExporter.BaseDirOverride = _root;
        var report = Path.Combine(Path.GetTempPath(), "fpstune-update-report-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DiagnosticReportExporter.ExportTo(report);
            using var zip = ZipFile.OpenRead(report);
            Assert.NotNull(zip.GetEntry("update-recovery.json"));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("launch.json") || e.FullName.Contains("settings-backups.zip") || e.FullName.EndsWith(".exe"));
        }
        finally { File.Delete(report); }
    }
    [Fact]
    public void Malformed_receipts_traversal_and_frozen_or_foreign_releases_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => UpdateRecovery.OwnedPath("..", "outside"));
        Assert.Throws<InvalidDataException>(() => UpdateRecovery.AttemptPath("../escape", "launch.json"));
        Assert.Throws<InvalidDataException>(() => UpdateRecovery.ValidateInfo(Info("1.6.2")));
        Assert.Throws<InvalidDataException>(() => UpdateRecovery.ValidateInfo(Info() with { InstallerUrl = "https://github.com/other/repo/setup.exe" }));
        var attempt = Attempt();
        File.WriteAllText(UpdateRecovery.AttemptPath(attempt.Id, "launch.json"), "{\"Id\":null}");
        Assert.Null(UpdateRecovery.Recent());
    }
    [Theory]
    [InlineData(0, "awaiting-startup", "upgrade")]
    [InlineData(2, "cancelled", "upgrade")]
    [InlineData(5, "cancelled", "upgrade")]
    [InlineData(4, "failed", "upgrade")]
    [InlineData(0, "succeeded", "rollback")]
    public void Real_independent_worker_waits_for_parent_locks_verified_file_records_exit_and_restarts_fixture(int code, string status, string mode)
    {
        var executable = CompileFixture();
        var app = Path.Combine(UpdateRecovery.InstallDirectoryOverride!, "FpsTune.exe");
        File.Copy(executable, app);
        var restartGate = app + ".hold";
        File.WriteAllText(restartGate, "keep the restarted fixture alive until explicitly released");
        Process? restarted = null;
        var parentPath = Path.Combine(_root, "parent.exe");
        File.Copy(executable, parentPath);
        using var parent = Process.Start(new ProcessStartInfo(parentPath) { UseShellExecute = false, CreateNoWindow = true })!;
        var package = Package(Info(mode == "rollback" ? "v0.2.7-beta" : "v0.2.8-beta"), File.ReadAllBytes(executable));
        var installer = UpdateRecovery.PackagePath(package);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(installer)!, "exit-code.txt"), code.ToString());
        var attempt = Attempt(package, parent, mode);
        try
        {
            UpdateWorker.Start(attempt, default);
            Assert.Equal("waiting-for-exit", UpdateRecovery.Recent()!.Status);
            Assert.False(File.Exists(installer + ".args"));
            Assert.Throws<IOException>(() => File.WriteAllText(installer, "replace verified file"));
            parent.Kill();
            Assert.True(parent.WaitForExit(5000));
            WaitUntil(() => UpdateRecovery.Recent()?.Status == status);
            WaitUntil(() => File.Exists(app + ".started"));
            // A startup marker does not mean the image has been unmapped. Own the exact
            // restarted fixture and keep it alive until the worker assertions finish.
            var identity = File.ReadAllText(app + ".started").Split('|');
            restarted = Process.GetProcessById(int.Parse(identity[0]));
            Assert.Equal(long.Parse(identity[1]), restarted.StartTime.ToUniversalTime().Ticks);
            Assert.Equal(app, restarted.MainModule!.FileName);
            Assert.Equal(code, UpdateRecovery.Recent()!.ExitCode);
            Assert.Contains("/SILENT /FpsTuneUpdate=1", File.ReadAllText(installer + ".args"));
            Assert.Contains("/DIR=", File.ReadAllText(installer + ".args"));
            Assert.NotNull(UpdateRecovery.ProtectedCopy(attempt));
            WaitUntil(() => !UpdateRecovery.ProcessMatches(UpdateRecovery.Recent()!.WorkerId, UpdateRecovery.Recent()!.WorkerStartedTicks));
            Assert.False(restarted.HasExited);
        }
        finally
        {
            File.Delete(restartGate);
            try
            {
                if (restarted is not null)
                {
                    using (restarted) Assert.True(restarted.WaitForExit(5000), "Restarted fixture did not exit before cleanup.");
                }
            }
            finally { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); } }
        }
    }
    [Fact]
    public void Real_worker_rejects_corrupt_executable_before_handover_and_never_runs_it()
    {
        var package = Package(Info(), File.ReadAllBytes(CompileFixture()));
        var attempt = Attempt(package);
        File.AppendAllText(UpdateRecovery.PackagePath(package), "corrupt");
        Assert.Throws<InvalidOperationException>(() => UpdateWorker.Start(attempt, default));
        Assert.Equal("failed", UpdateRecovery.Recent()!.Status);
        Assert.Equal("verify-package", UpdateRecovery.Recent()!.Stage);
        Assert.False(File.Exists(UpdateRecovery.PackagePath(package) + ".args"));
    }
    [Fact]
    public void Cancelling_real_handover_keeps_parent_alive_and_never_launches_installer()
    {
        var executable = CompileFixture();
        var parentPath = Path.Combine(_root, "parent.exe");
        File.Copy(executable, parentPath);
        using var parent = Process.Start(new ProcessStartInfo(parentPath) { UseShellExecute = false, CreateNoWindow = true })!;
        var package = Package(Info(), File.ReadAllBytes(executable));
        var attempt = Attempt(package, parent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(() => UpdateWorker.Start(attempt, cancellation.Token));
            WaitUntil(() => UpdateRecovery.Recent()?.Status == "cancelled");
            Assert.False(parent.HasExited);
            Assert.False(File.Exists(UpdateRecovery.PackagePath(package) + ".args"));
            WaitUntil(() => !UpdateRecovery.ProcessMatches(UpdateRecovery.Recent()!.WorkerId, UpdateRecovery.Recent()!.WorkerStartedTicks));
        }
        finally { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); } }
    }
    private string CompileFixture()
    {
        // Deliberately never runs an Inno installer or writes an uninstall registration.
        var source = Path.Combine(_root, "fixture.cs");
        var output = Path.Combine(_root, "fixture.exe");
        File.WriteAllText(source, """
            using System;
            using System.IO;
            using System.Diagnostics;
            using System.Threading;
            using System.Reflection;
            [assembly: AssemblyInformationalVersion("0.2.7-beta")]
            class Fixture {
                static int Main(string[] args) {
                    string path = Process.GetCurrentProcess().MainModule.FileName;
                    if (Path.GetFileName(path) == "parent.exe") { Thread.Sleep(30000); return 0; }
                    string code = Path.Combine(Path.GetDirectoryName(path), "exit-code.txt");
                    if (File.Exists(code)) { File.WriteAllText(path + ".args", String.Join(" ", args)); return Int32.Parse(File.ReadAllText(code)); }
                    using (var current = Process.GetCurrentProcess())
                        File.WriteAllText(path + ".started.tmp", current.Id + "|" + current.StartTime.ToUniversalTime().Ticks);
                    File.Move(path + ".started.tmp", path + ".started");
                    while (File.Exists(path + ".hold")) Thread.Sleep(20);
                    return 0;
                }
            }
            """, Encoding.UTF8);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        using var process = Process.Start(new ProcessStartInfo(compiler)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/nologo /target:winexe /out:\"{output}\" \"{source}\""
        })!;
        var standard = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(15000), "Fixture compiler did not finish.");
        Assert.True(process.ExitCode == 0, standard.Result + error.Result);
        return output;
    }
    private static void WaitUntil(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!complete())
        {
            var attempt = UpdateRecovery.Recent();
            var errorFile = attempt is null ? null : UpdateRecovery.AttemptPath(attempt.Id, "worker-error.txt");
            Assert.True(DateTime.UtcNow < deadline, "Independent update worker did not finish: " + JsonSerializer.Serialize(attempt)
                + (errorFile is not null && File.Exists(errorFile) ? File.ReadAllText(errorFile) : ""));
            Thread.Sleep(50);
        }
    }
}
