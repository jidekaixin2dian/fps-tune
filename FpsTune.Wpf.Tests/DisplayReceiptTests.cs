using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 驱动设置的逐项回执与高级参数三态读取。
/// 关键契约：一条设置读回不符不再让整批失败；「驱动不支持」与「读回不符」分开报；
/// 失败项能单独重试；「驱动没有这项」「继承值」「本 profile 覆盖」必须区分得出来。
/// </summary>
[Collection("BackupService serial")]
public sealed class DisplayReceiptTests : IDisposable
{
    private const string Exe = "FpsTune-DrsReceipt-test.exe";
    private readonly DisplayQualityTests.FakeNvdrsApi _api = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fpstune-drs-receipt-" + Guid.NewGuid().ToString("N"));

    public DisplayReceiptTests()
    {
        DisplayQualityService.ApiOverride = () => _api;
        DisplayQualityService.BackupDirOverride = _directory;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        DisplayQualityService.ApiOverride = null;
        DisplayQualityService.BackupDirOverride = null;
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Competitive_preset_reports_applied_and_unsupported_items_without_throwing()
    {
        var profile = _api.AddProfile("existing", Exe);
        Assert.Equal(9, DisplayQualityService.CompetitiveRecipe(desktopHighEndGpu: false).Count);
        _api.DropOnSave.Add(DisplayQualityService.ShaderDiskCacheId);

        var receipt = DisplayQualityService.ApplyCompetitivePreset(Exe, desktopHighEndGpu: false);

        Assert.Equal(9, receipt.Items.Count);
        Assert.Equal(8, receipt.AppliedCount);
        var unsupported = Assert.Single(receipt.Unsupported);
        Assert.Equal(DisplayQualityService.ShaderDiskCacheId, unsupported.SettingId);
        Assert.Empty(receipt.Mismatched);
        Assert.False(receipt.Ok);
        // 其余 8 项确实写进了驱动，原值备份也留着。
        Assert.Equal(16u, profile.Settings[DisplayQualityService.AnisoLevelId]);
        Assert.False(profile.Settings.ContainsKey(DisplayQualityService.ShaderDiskCacheId));
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
        // 界面直接拿这个名字列清单，不能是空串。
        Assert.Equal(NvdrsSettingCatalog.DisplayName(DisplayQualityService.ShaderDiskCacheId), unsupported.Name);
    }

    [Fact]
    public void Retrying_only_failed_items_writes_those_and_leaves_the_rest_alone()
    {
        var profile = _api.AddProfile("existing", Exe);
        _api.DropOnSave.Add(DisplayQualityService.ShaderDiskCacheId);
        var first = DisplayQualityService.ApplyCompetitivePreset(Exe, desktopHighEndGpu: false);
        Assert.Equal(1, first.FailedCount);
        Assert.Equal(new[] { DisplayQualityService.ShaderDiskCacheId }, first.Expected.Keys);
        var untouched = profile.Settings[DisplayQualityService.TextureQualityId];

        _api.DropOnSave.Clear();
        var retry = DisplayQualityService.RetryFailedSettings(Exe, first);

        Assert.True(retry.Ok);
        Assert.Equal(1, retry.AppliedCount);
        Assert.Equal(1u, profile.Settings[DisplayQualityService.ShaderDiskCacheId]);
        Assert.Equal(untouched, profile.Settings[DisplayQualityService.TextureQualityId]);
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
        // 还原仍然回到「从未覆盖」的原点。
        Assert.True(DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.Empty(profile.Settings);
        // 没有失败项时重试什么都不写。
        Assert.Empty(DisplayQualityService.RetryFailedSettings(Exe, retry).Items);
    }

    [Fact]
    public void Failed_driver_readback_keeps_the_previous_success_restorable()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = (uint)DlssPreset.PresetE;
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK).RequireApplied();
        _api.SimulateSaveIgnored = true;

        var failed = DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetJ);
        Assert.Throws<InvalidOperationException>(failed.RequireApplied);
        Assert.Equal((uint)DlssPreset.PresetK, profile.Settings[DisplayQualityService.DlssSrPresetId]);

        _api.SimulateSaveIgnored = false;
        Assert.True(DisplayQualityService.RestoreDlssSettings(Exe));
        Assert.Equal((uint)DlssPreset.PresetE, profile.Settings[DisplayQualityService.DlssSrPresetId]);
    }

    [Fact]
    public void Following_game_dlss_keeps_other_driver_overrides_and_their_backups()
    {
        var profile = _api.AddProfile("existing", Exe);
        DisplayQualityService.ApplyFrameLimits(Exe, new(120, null)).RequireApplied();
        Assert.False(DisplayQualityService.GetDlssState(Exe).Restorable);
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK).RequireApplied();

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.FollowGame).RequireApplied();

        Assert.DoesNotContain(DisplayQualityService.DlssSrPresetId, profile.Settings.Keys);
        Assert.Equal(120u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
        Assert.True(DisplayQualityService.GetDrsGameSettings(Exe).FrameLimitsRestorable);
    }

    [Fact]
    public void Following_game_dlss_preserves_external_values_and_distinguishes_inheritance()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = (uint)DlssPreset.PresetE;

        Assert.Empty(DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.FollowGame).Items);
        Assert.Equal((uint)DlssPreset.PresetE, profile.Settings[DisplayQualityService.DlssSrPresetId]);

        profile.Settings.Remove(DisplayQualityService.DlssSrPresetId);
        profile.InheritedSettings[DisplayQualityService.DlssSrPresetId] = (uint)DlssPreset.PresetJ;
        Assert.False(DisplayQualityService.GetDlssState(Exe).Covered);
        Assert.Equal((uint)DlssPreset.PresetJ, DisplayQualityService.GetDlssState(Exe).PresetValue);
    }

    [Fact]
    public void Advanced_read_distinguishes_missing_inherited_and_overridden()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.TextureQualityId] = 10;
        profile.InheritedSettings[DisplayQualityService.PowerModeId] = (uint)PowerMode.OptimalPower;

        var snapshot = DisplayQualityService.ReadAdvancedSettings(Exe);

        Assert.True(snapshot.ProfileFound);
        Assert.Equal(NvdrsSettingCatalog.All.Count, snapshot.Items.Count);
        var texture = snapshot.Items.Single(i => i.SettingId == DisplayQualityService.TextureQualityId);
        Assert.Equal(NvdrsSettingState.Overridden, texture.State);
        Assert.Equal(10u, texture.Value);
        var power = snapshot.Items.Single(i => i.SettingId == DisplayQualityService.PowerModeId);
        Assert.Equal(NvdrsSettingState.Inherited, power.State);
        Assert.Equal((uint)PowerMode.OptimalPower, power.Value);
        Assert.Equal(NvdrsSettingState.Missing,
            snapshot.Items.Single(i => i.SettingId == DisplayQualityService.VSyncModeId).State);
        // 驱动没上报默认值时回落到目录里已确认的默认值，不显示空白。
        Assert.Equal((uint)AnisoLevel.AppControlled,
            snapshot.Items.Single(i => i.SettingId == DisplayQualityService.AnisoLevelId).DriverDefault);
    }

    [Fact]
    public void Advanced_read_for_unregistered_game_says_no_profile_and_guesses_nothing()
    {
        var snapshot = DisplayQualityService.ReadAdvancedSettings(Exe);
        Assert.False(snapshot.ProfileFound);
        Assert.All(snapshot.Items, item => Assert.Equal(NvdrsSettingState.Missing, item.State));
    }

    [Fact]
    public void Advanced_write_and_restore_to_inherited_go_through_backup_and_readback()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.InheritedSettings[DisplayQualityService.TextureQualityId] = (uint)TextureFilterQuality.Quality;

        var write = DisplayQualityService.SetAdvancedSetting(Exe, DisplayQualityService.TextureQualityId,
            (uint)TextureFilterQuality.HighQuality);
        Assert.True(write.Ok);
        Assert.Equal((uint)TextureFilterQuality.HighQuality,
            profile.Settings[DisplayQualityService.TextureQualityId]);
        Assert.Equal(NvdrsSettingState.Overridden, DisplayQualityService.ReadAdvancedSettings(Exe)
            .Items.Single(i => i.SettingId == DisplayQualityService.TextureQualityId).State);

        var reset = DisplayQualityService.SetAdvancedSetting(Exe, DisplayQualityService.TextureQualityId, null);
        Assert.True(reset.Ok);
        Assert.False(profile.Settings.ContainsKey(DisplayQualityService.TextureQualityId));
        Assert.Equal(NvdrsSettingState.Inherited, DisplayQualityService.ReadAdvancedSettings(Exe)
            .Items.Single(i => i.SettingId == DisplayQualityService.TextureQualityId).State);

        // 写前备份：还原回到「继承」，不是回到我们写的值。
        Assert.True(DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.Equal((uint)TextureFilterQuality.Quality,
            profile.InheritedSettings[DisplayQualityService.TextureQualityId]);
        Assert.False(DisplayQualityService.HasRestorableBackup(Exe));
    }

    [Fact]
    public void Advanced_write_rejects_unknown_ids_and_values_outside_the_catalog()
    {
        _api.AddProfile("existing", Exe);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DisplayQualityService.SetAdvancedSetting(Exe, DisplayQualityService.TextureQualityId, 7));
        Assert.ThrowsAny<ArgumentException>(() =>
            DisplayQualityService.SetAdvancedSetting(Exe, 0xDEADBEEFu, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DisplayQualityService.SetAdvancedSetting(Exe, DisplayQualityService.FrameRateLimitId, 5000));
    }

    [Fact]
    public void Catalog_covers_only_managed_settings_with_consistent_choices()
    {
        Assert.NotEmpty(NvdrsSettingCatalog.All);
        Assert.Equal(NvdrsSettingCatalog.All.Count,
            NvdrsSettingCatalog.All.Select(d => d.SettingId).Distinct().Count());
        foreach (var definition in NvdrsSettingCatalog.All)
        {
            Assert.True(DisplayQualityService.IsManaged(definition.SettingId),
                $"未受管的设置项 0x{definition.SettingId:X8} 不该出现在高级参数表里");
            Assert.NotEmpty(definition.NameKey);
            Assert.NotEmpty(definition.NoteKey);
            if (definition.IsChoice)
            {
                Assert.NotEmpty(definition.Choices);
                Assert.Equal(definition.Choices.Count, definition.Choices.Select(c => c.Value).Distinct().Count());
                Assert.All(definition.Choices, choice =>
                    Assert.True(NvdrsSettingCatalog.IsValid(definition, choice.Value)));
                if (definition.DriverDefault is { } fallback)
                    Assert.True(NvdrsSettingCatalog.IsValid(definition, fallback));
            }
            else
            {
                Assert.True(definition.Min < definition.Max);
                Assert.True(NvdrsSettingCatalog.IsValid(definition, definition.Min));
                Assert.False(NvdrsSettingCatalog.IsValid(definition, definition.Max + 1));
            }
        }
        // 未收录的 ID 仍然有可读名字，不会在界面上留空。
        Assert.NotEqual("", NvdrsSettingCatalog.DisplayName(0xDEADBEEFu));
    }

    [Fact]
    public void External_change_and_missing_persistence_are_reported_as_different_problems()
    {
        var profile = _api.AddProfile("existing", Exe);
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 10;   // 别的工具改过

        var drift = Assert.Throws<InvalidOperationException>(() => DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.Contains("Str.DrsExternalChanged", drift.Message);
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));

        const string other = "FpsTune-DrsReceipt-other.exe";
        var second = _api.AddProfile("second", other);
        second.Settings[DisplayQualityService.TextureQualityId] = 10;
        DisplayQualityService.ApplyTextureQuality(other, TextureFilterQuality.HighQuality);
        _api.SimulateSaveIgnored = true;

        var notPersisted = Assert.Throws<InvalidOperationException>(() =>
            DisplayQualityService.RemoveDlssOverride(other));
        Assert.Contains("Str.DrsWriteNotPersisted", notPersisted.Message);
        Assert.NotEqual(drift.Message, notPersisted.Message);
        Assert.True(DisplayQualityService.HasRestorableBackup(other));

        _api.SimulateSaveIgnored = false;
        Assert.True(DisplayQualityService.RemoveDlssOverride(other));
        Assert.Equal(10u, second.Settings[DisplayQualityService.TextureQualityId]);
    }
}
