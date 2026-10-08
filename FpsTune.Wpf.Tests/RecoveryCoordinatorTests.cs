using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class RecoveryCoordinatorTests
{
    [Fact]
    public void Failed_modules_do_not_block_other_scopes_and_backups_are_preserved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpstune-recovery-" + Guid.NewGuid().ToString("N"));
        BackupService.BackupDirOverride = dir;
        DisplayQualityService.BackupDirOverride = Path.Combine(dir, "drs");
        DigitalVibranceService.BackupDirOverride = Path.Combine(dir, "vib");
        IccFilterService.BackupDirOverride = Path.Combine(dir, "icc");
        var api = new VibranceApi();
        DigitalVibranceService.ApiOverride = () => api;
        try
        {
            Directory.CreateDirectory(DisplayQualityService.BackupDirOverride);
            File.WriteAllText(Path.Combine(dir, "csharp-backup-invalid.json"), "[{\"Id\":\"bad\",\"Kind\":\"unknown\"}]");
            var corruptDriver = Path.Combine(DisplayQualityService.BackupDirOverride, "backup-game.exe.json");
            File.WriteAllText(corruptDriver, "{broken");
            DigitalVibranceService.SetPercent(70);
            Assert.Equal(70, api.Current);
            var inventory = RecoveryCoordinator.Inventory();
            Assert.Contains(inventory, scope => scope.Module == "nvidia" && scope.Target == "game.exe");
            Assert.Contains(inventory, scope => scope.Module == "vibrance");
            Assert.True(RecoveryCoordinator.NeedsAdmin());
            var result = RecoveryCoordinator.RestoreAll();
            Assert.Equal(10, api.Current);
            Assert.Equal(2, result.Failures.Count);
            Assert.Contains(result.Restored, item => item.Id == "Digital Vibrance");
            Assert.Equal("{broken", File.ReadAllText(corruptDriver));
        }
        finally
        {
            BackupService.BackupDirOverride = null;
            DisplayQualityService.BackupDirOverride = null;
            DigitalVibranceService.BackupDirOverride = null;
            IccFilterService.BackupDirOverride = null;
            DigitalVibranceService.ApiOverride = null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private sealed class VibranceApi : INvibranceApi
    {
        public int Current { get; set; } = 10;
        public bool TryInitialize() => true;
        public string? LastError => null;
        public bool TryGetInfo(out int current, out int min, out int max, out int defaultValue)
        { current = Current; min = 0; max = 100; defaultValue = 50; return true; }
        public string? DisplayIdentity() => "isolated-display";
        public void SetLevel(int level) => Current = level;
        public void Dispose() { }
    }
}
