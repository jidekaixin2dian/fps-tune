using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record RecoveryScope(string Module, string Target, bool NeedsAdmin, string? Error = null);

/// <summary>系统、按游戏驱动、ICC、数字振动共享完整的恢复入口；一项失败不阻断其他模块。</summary>
public static class RecoveryCoordinator
{
    public static IReadOnlyList<RecoveryScope> Inventory()
    {
        var scopes = new List<RecoveryScope>();
        void Read(string module, Func<IEnumerable<RecoveryScope>> read)
        {
            try { scopes.AddRange(read()); }
            catch (Exception ex) { scopes.Add(new(module, "", false, PrivacyScrub.Sanitize(ex.Message))); }
        }
        Read("system", () => BackupService.ListBackupStatuses().Where(s => !s.Valid || s.PendingCount > 0)
            .Select(s => new RecoveryScope("system", s.FileName, BackupService.RestoreNeedsAdmin(), s.Valid ? null : Str.T("Str.BackupFormatInvalid"))));
        Read("nvidia", () => DisplayQualityService.BackupGames().Select(game => new RecoveryScope("nvidia", game, true)));
        Read("icc", () => IccFilterService.HasRestorableBackup() ? new[] { new RecoveryScope("icc", Str.T("Str.RecoveryDisplay"), false) } : []);
        Read("vibrance", () => DigitalVibranceService.HasRestorableBackup() ? new[] { new RecoveryScope("vibrance", Str.T("Str.RecoveryDisplay"), false) } : []);
        return scopes;
    }

    public static bool NeedsAdmin() => Inventory().Any(s => s.NeedsAdmin);

    internal static BackupService.RestoreAllResult RestoreAll()
    {
        using var gate = SystemMutationGate.Acquire();
        var result = BackupService.RestoreAll();
        void Recover(string module, Func<bool> restore)
        {
            try { if (restore()) result.Restored.Add((module, module)); }
            catch (Exception ex) { result.Failures.Add(module + ": " + PrivacyScrub.Sanitize(ex.Message)); }
        }
        try
        {
            foreach (var game in DisplayQualityService.BackupGames())
                Recover("NVIDIA / " + game, () => DisplayQualityService.RemoveDlssOverride(game));
        }
        catch (Exception ex) { result.Failures.Add("NVIDIA: " + PrivacyScrub.Sanitize(ex.Message)); }
        Recover("ICC", IccFilterService.Restore);
        Recover("Digital Vibrance", DigitalVibranceService.Restore);
        return result;
    }
}
