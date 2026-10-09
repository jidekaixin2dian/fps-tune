using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class DrsTransactionTests : IDisposable
{
    private const string Exe = "FpsTune-DrsTransaction-test.exe";
    private readonly DisplayQualityTests.FakeNvdrsApi _api = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fpstune-drs-transaction-" + Guid.NewGuid().ToString("N"));
    private string BackupPath => Path.Combine(_directory, "backup-" + Exe + ".json");
    private string HistoryPath => Path.Combine(_directory, "history");
    public DrsTransactionTests()
    {
        DisplayQualityService.ApiOverride = () => _api;
        DisplayQualityService.BackupDirOverride = _directory;
        Directory.CreateDirectory(_directory);
    }
    public void Dispose()
    {
        DisplayQualityService.ApiOverride = null;
        DisplayQualityService.BackupDirOverride = null;
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Apply_K_after_driver_reset_archives_stale_receipt_and_restores_current_baseline()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 10;
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        var oldReceipt = File.ReadAllText(BackupPath);
        profile.Settings.Clear();
        profile.Settings[0x12345678] = 42;

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);

        Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        Assert.Equal(oldReceipt, File.ReadAllText(Assert.Single(Directory.GetFiles(HistoryPath, "*.json"))));
        Assert.True(DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.False(profile.Settings.ContainsKey(DisplayQualityService.DlssSrPresetId));
        Assert.Equal(42u, Assert.Single(profile.Settings).Value);
    }

    [Fact]
    public void Reapply_backs_up_external_model_but_keeps_other_owned_settings_restorable()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 5;
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        DisplayQualityService.ApplyPowerMode(Exe, PowerMode.PreferMax);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 10;

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Equal(10u, Assert.Single(profile.Settings).Value);
        Assert.Single(Directory.GetFiles(HistoryPath, "*.json"));
    }

    [Fact]
    public void Unrelated_drift_does_not_block_K_or_get_overwritten_by_restore()
    {
        var profile = _api.AddProfile("existing", Exe);
        DisplayQualityService.ApplyTextureQuality(Exe, TextureFilterQuality.HighQuality);
        DisplayQualityService.ApplyPowerMode(Exe, PowerMode.PreferMax);
        profile.Settings[DisplayQualityService.TextureQualityId] = 10;

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        Assert.Equal(10u, profile.Settings[DisplayQualityService.TextureQualityId]);
        Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Equal(new KeyValuePair<uint, uint>(DisplayQualityService.TextureQualityId, 10u), Assert.Single(profile.Settings));
    }

    [Fact]
    public void History_write_failure_prevents_save_and_preserves_current_value_and_receipt()
    {
        var profile = _api.AddProfile("existing", Exe);
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 10;
        var oldReceipt = File.ReadAllText(BackupPath);
        File.WriteAllText(HistoryPath, "blocked directory");

        Assert.ThrowsAny<IOException>(() => DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK));
        Assert.Equal(1, _api.SaveCount);
        Assert.Equal(10u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        Assert.Equal(oldReceipt, File.ReadAllText(BackupPath));
    }

    [Fact]
    public void New_apply_archives_legacy_receipt_and_restores_only_current_originals()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 10;
        var legacy = $$"""{"GameExe":"{{Exe}}","OwnProfile":false,"Settings":[{"SettingId":{{DisplayQualityService.DlssSrPresetId}},"Existed":true,"Value":5}]}""";
        File.WriteAllText(BackupPath, legacy);
        Assert.Throws<InvalidDataException>(() => DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.False(_api.SaveCalled);

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        Assert.Equal(legacy, File.ReadAllText(Assert.Single(Directory.GetFiles(HistoryPath, "*.json"))));
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Equal(10u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
    }

    private static readonly DisplayQualityService.DriverSettingsSelection Batch = new(
        TextureFilterQuality.HighQuality, PowerMode.PreferMax, TransparencyAa.Off, 1,
        AnisoLevel.Level16, VSyncMode.ForceOff, true);

    [Fact]
    public void Full_3D_selection_saves_once_and_restores_exact_original_overrides()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 10;
        profile.Settings[DisplayQualityService.TextureQualityId] = 10;
        profile.Settings[DisplayQualityService.PowerModeId] = 0;
        profile.Settings[0x12345678] = 42;
        var original = profile.Settings.OrderBy(p => p.Key).ToArray();

        DisplayQualityService.ApplyDriverSettings(Exe, Batch);

        Assert.Equal(1, _api.SaveCount);
        Assert.Equal((uint)TextureFilterQuality.HighQuality, profile.Settings[DisplayQualityService.TextureQualityId]);
        Assert.Equal((uint)PowerMode.PreferMax, profile.Settings[DisplayQualityService.PowerModeId]);
        Assert.Equal(0u, profile.Settings[DisplayQualityService.TransparencyMultisampleId]);
        Assert.Equal(0u, profile.Settings[DisplayQualityService.TransparencySupersampleId]);
        Assert.Equal(1u, profile.Settings[DisplayQualityService.PreRenderLimitId]);
        Assert.Equal(DisplayQualityService.AnisoSelectorUser, profile.Settings[DisplayQualityService.AnisoSelectorId]);
        Assert.Equal(16u, profile.Settings[DisplayQualityService.AnisoLevelId]);
        Assert.Equal(DisplayQualityService.VSyncForceOff, profile.Settings[DisplayQualityService.VSyncModeId]);
        Assert.Equal(1u, profile.Settings[DisplayQualityService.ShaderDiskCacheId]);
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Equal(original, profile.Settings.OrderBy(p => p.Key).ToArray());
    }

    [Fact]
    public void Full_3D_save_denied_never_commits_partial_choices_and_keeps_backup()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.PowerModeId] = 0;
        _api.SimulateSaveDenied = true;
        Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyDriverSettings(Exe, Batch));
        Assert.Equal(1, _api.SaveCount);
        Assert.Equal(new KeyValuePair<uint, uint>(DisplayQualityService.PowerModeId, 0u), Assert.Single(profile.Settings));
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
    }

    [Fact]
    public void Full_3D_staging_error_never_saves_earlier_choices()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.PowerModeId] = 0;
        _api.FailSettingId = DisplayQualityService.ShaderDiskCacheId;
        Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyDriverSettings(Exe, Batch));
        Assert.False(_api.SaveCalled);
        Assert.Equal(new KeyValuePair<uint, uint>(DisplayQualityService.PowerModeId, 0u), Assert.Single(profile.Settings));
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
    }

    [Fact]
    public void Incomplete_schema2_receipt_cannot_be_rebased_or_restored()
    {
        _api.AddProfile("existing", Exe);
        File.WriteAllText(BackupPath,
            $$"""{"GameExe":"{{Exe}}","SchemaVersion":2,"Settings":[{"SettingId":{{DisplayQualityService.DlssSrPresetId}},"Existed":false,"Value":0}],"PostSettings":[]}""");
        Assert.Throws<InvalidDataException>(() => DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK));
        Assert.Throws<InvalidDataException>(() => DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.False(_api.SaveCalled);
        Assert.False(Directory.Exists(HistoryPath));
    }

    [Fact]
    public void Failed_reapply_can_restore_the_original_before_the_previous_success()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.TextureQualityId] = 10;
        var original = profile.Settings.OrderBy(p => p.Key).ToArray();
        DisplayQualityService.ApplyDriverSettings(Exe, Batch);
        _api.FailSettingId = DisplayQualityService.ShaderDiskCacheId;
        Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyDriverSettings(Exe,
            Batch with { TextureQuality = TextureFilterQuality.Quality, PowerMode = PowerMode.OptimalPower }));
        Assert.Equal(1, _api.SaveCount);

        Assert.True(DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.Equal(original, profile.Settings.OrderBy(p => p.Key).ToArray());
    }

    [Fact]
    public void Unrelated_apply_after_failed_reapply_keeps_original_recovery_and_avoids_false_drift()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.TextureQualityId] = 10;
        DisplayQualityService.ApplyTextureQuality(Exe, TextureFilterQuality.HighQuality);
        _api.SimulateSaveDenied = true;
        Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyTextureQuality(Exe, TextureFilterQuality.Quality));
        _api.SimulateSaveDenied = false;
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        Assert.False(Directory.Exists(HistoryPath));
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Equal(new KeyValuePair<uint, uint>(DisplayQualityService.TextureQualityId, 10u), Assert.Single(profile.Settings));
    }
}
