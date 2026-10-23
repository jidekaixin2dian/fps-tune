using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>Only explicit installation/recovery actions hand over to the independent worker.</summary>
internal static class UpdateInstaller
{
    private static readonly SemaphoreSlim Active = new(1, 1);
    internal static Task InstallAsync(UpdateInfo info, Action<double> progress, CancellationToken token, Action<string>? stage = null)
        => RunAsync(info, null, "upgrade", progress, token, stage);
    internal static async Task RetryAsync(UpdateAttempt attempt, Action<double> progress, CancellationToken token, Action<string>? stage = null)
    {
        if (UpdateRecovery.Busy(attempt)) throw new InvalidOperationException(Str.T("Str.UpdateInProgress"));
        var target = await ReleaseAsync(attempt.Target.ReleaseTag!, token)
            ?? throw new InvalidOperationException(Str.T("Str.UpdateAssetsMissing"));
        await RunAsync(target, attempt.PreviousPackageId, attempt.Mode, progress, token, stage);
    }
    internal static async Task RollbackAsync(UpdateAttempt attempt, Action<double> progress, CancellationToken token, Action<string>? stage = null)
    {
        if (UpdateRecovery.Busy(attempt)) throw new InvalidOperationException(Str.T("Str.UpdateInProgress"));
        if (attempt.PreviousPackageId is null) throw new InvalidOperationException(Str.T("Str.UpdateRollbackUnavailable"));
        var previous = UpdateRecovery.LoadPackage(attempt.PreviousPackageId);
        var target = await ReleaseAsync(previous.Tag, token)
            ?? throw new InvalidOperationException(Str.T("Str.UpdateRollbackUnavailable"));
        await RunAsync(target, null, "rollback", progress, token, stage);
    }
    private static Task<UpdateInfo?> ReleaseAsync(string version, CancellationToken token)
        => UpdateRecovery.ReleaseOverride?.Invoke(version, token) ?? UpdateService.GetReleaseAsync(version, token);

    private static async Task RunAsync(UpdateInfo info, string? previousId, string mode, Action<double> progress, CancellationToken token, Action<string>? stage)
    {
        UpdateRecovery.ValidateInfo(info);
        if (!await Active.WaitAsync(0, token)) throw new InvalidOperationException(Str.T("Str.UpdateInProgress"));
        UpdateAttempt? attempt = null;
        string? unpublishedId = null;
        try
        {
            using var cacheLease = UpdateRecovery.AcquireCacheLease();
            EnsureIdle();
            var recent = UpdateRecovery.Recent();
            if (recent is not null && UpdateRecovery.Busy(recent)) throw new InvalidOperationException(Str.T("Str.UpdateInProgress"));
            await Task.Run(() => UpdateRecovery.PruneCacheOwned(), token);
            stage?.Invoke(Str.T("Str.UpdateDownloading"));
            progress(-1);
            var package = await UpdateRecovery.GetPackageAsync(info, progress, token);
            if (mode == "upgrade" && previousId is null)
            {
                // A missing/offline previous release removes rollback availability, but does not block a verified upgrade.
                try
                {
                    stage?.Invoke(Str.T("Str.UpdatePreparingRollback"));
                    progress(-1);
                    var previous = await ReleaseAsync(UpdateRecovery.CurrentVersion, token);
                    if (previous is not null && previous.ReleaseTag != info.ReleaseTag)
                        previousId = (await UpdateRecovery.GetPackageAsync(previous, progress, token)).Id;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { previousId = null; }
            }
            token.ThrowIfCancellationRequested();
            stage?.Invoke(Str.T("Str.UpdatePreparingInstall"));
            progress(-1);
            // The mutex is acquired and released on this one synchronous worker thread.
            await Task.Run(() =>
            {
                using var gate = SystemMutationGate.Acquire(TimeSpan.Zero);
                EnsureIdle();
                token.ThrowIfCancellationRequested();
                using var verified = new FileStream(UpdateRecovery.PackagePath(package), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!Convert.ToHexString(SHA256.HashData(verified)).Equals(package.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Str.T("Str.InstallerHashMismatch"));
                using var parent = Process.GetCurrentProcess();
                var id = Guid.NewGuid().ToString("N");
                unpublishedId = id;
                var directory = Path.GetFullPath(UpdateRecovery.InstallDirectoryOverride
                    ?? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
                if (directory.Contains('"')) throw new InvalidDataException(Str.T("Str.UpdateUrlInvalid"));
                var snapshotHash = UpdateRecovery.Snapshot(id);
                token.ThrowIfCancellationRequested();
                attempt = new(id, UpdateRecovery.CurrentVersion, info, previousId, package.Id, directory, parent.Id,
                    parent.StartTime.ToUniversalTime().Ticks, snapshotHash, DateTimeOffset.UtcNow, package.Hash, mode);
                UpdateRecovery.SaveAttempt(attempt);
                unpublishedId = null;
                if (UpdateRecovery.WorkerOverride is not null) UpdateRecovery.WorkerOverride(attempt, token).GetAwaiter().GetResult();
                else UpdateWorker.Start(attempt, token);
            }, token);
            EnsureIdle();
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            if (unpublishedId is not null) UpdateRecovery.DiscardUnpublishedAttempt(unpublishedId);
            if (attempt is not null)
            {
                try { File.WriteAllText(UpdateRecovery.AttemptPath(attempt.Id, "abort"), "abort"); }
                catch { }
                try { UpdateRecovery.SaveOutcome(new(attempt.Id, ex is OperationCanceledException ? "cancelled" : "failed",
                    "handoff", Error: OperationDiagnostics.Scrub(ex.Message))); }
                catch { /* Preserve the original failure. Startup can classify an interrupted receipt. */ }
            }
            throw;
        }
        finally
        {
            try { await Task.Run(UpdateRecovery.PruneCache); }
            finally { Active.Release(); }
        }
    }
    private static void EnsureIdle()
    {
        if (App.SessionService.IsRunning || ExperimentRunner.IsRunning)
            throw new InvalidOperationException(Str.T("Str.UpdateSessionBusy"));
    }
}
