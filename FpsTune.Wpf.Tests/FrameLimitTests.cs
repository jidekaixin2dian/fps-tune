using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class FrameLimitTests : IDisposable
{
    private const string Exe = "FrameLimitFixture.exe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-frl-" + Guid.NewGuid().ToString("N"));
    private readonly DisplayQualityTests.FakeNvdrsApi _api = new();
    public FrameLimitTests()
    {
        Directory.CreateDirectory(_root);
        DisplayQualityService.ApiOverride = () => _api;
        DisplayQualityService.BackupDirOverride = _root;
    }
    public void Dispose()
    {
        DisplayQualityService.ApiOverride = null; DisplayQualityService.BackupDirOverride = null;
        Directory.Delete(_root, recursive: true);
    }
    [Fact]
    public void Two_caps_commit_once_and_partial_restore_preserves_other_tool_changes_and_inheritance()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.FrameRateLimitId] = 120;
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 5;
        profile.InheritedSettings[DisplayQualityService.BackgroundFrameRateLimitId] = 60;
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        DisplayQualityService.ApplyFrameLimits(Exe, new(144, 30));
        Assert.Equal(2, _api.SaveCount);
        var state = DisplayQualityService.GetDrsGameSettings(Exe);
        Assert.Equal(144u, state.FrameRateLimit); Assert.Equal(30u, state.BackgroundFrameRateLimit);
        Assert.True(state.FrameLimitsRestorable);
        Assert.True(DisplayQualityService.RestoreFrameLimits(Exe));
        Assert.Equal(120u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
        Assert.False(profile.Settings.ContainsKey(DisplayQualityService.BackgroundFrameRateLimitId));
        Assert.Equal(60u, DisplayQualityService.GetDrsGameSettings(Exe).BackgroundFrameRateLimit);
        Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
        Assert.False(DisplayQualityService.GetDrsGameSettings(Exe).FrameLimitsRestorable);
        Assert.False(DisplayQualityService.RestoreFrameLimits(Exe));
        Assert.True(DisplayQualityService.RemoveDlssOverride(Exe));
        Assert.Equal(5u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        Assert.Equal(120u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
    }
    [Fact]
    public void Unselected_cap_is_untouched_and_zero_is_a_real_disable_override()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.FrameRateLimitId] = 240;
        DisplayQualityService.ApplyFrameLimits(Exe, new(null, 0));
        Assert.Equal(240u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
        Assert.Equal(0u, profile.Settings[DisplayQualityService.BackgroundFrameRateLimitId]);
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Equal(new KeyValuePair<uint, uint>(DisplayQualityService.FrameRateLimitId, 240), Assert.Single(profile.Settings));
    }
    [Theory]
    [InlineData(19, 30)] [InlineData(1001, 30)] [InlineData(144, 19)] [InlineData(144, 201)]
    public void Invalid_caps_do_not_create_receipts_or_commit_partial_values(uint main, uint background)
    {
        _api.AddProfile("existing", Exe);
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayQualityService.ApplyFrameLimits(Exe, new(main, background)));
        Assert.Equal(0, _api.SaveCount); Assert.Empty(Directory.GetFiles(_root));
    }
    [Fact]
    public void Empty_selection_does_not_save_and_denial_keeps_the_original_values_restorable()
    {
        var profile = _api.AddProfile("existing", Exe);
        Assert.Throws<ArgumentException>(() => DisplayQualityService.ApplyFrameLimits(Exe, new(null, null)));
        profile.Settings[DisplayQualityService.FrameRateLimitId] = 60;
        _api.SimulateSaveDenied = true;
        Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyFrameLimits(Exe, new(144, 30)));
        Assert.Equal(60u, Assert.Single(profile.Settings).Value);
        Assert.True(DisplayQualityService.GetDrsGameSettings(Exe).FrameLimitsRestorable);
    }
    [Fact]
    public void Ignored_save_is_detected_and_external_cap_changes_are_never_overwritten_by_restore()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.FrameRateLimitId] = 60;
        _api.SimulateSaveIgnored = true;
        Assert.Throws<InvalidOperationException>(() => DisplayQualityService.ApplyFrameLimits(Exe, new(144, null)));
        Assert.Equal(60u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
        _api.SimulateSaveIgnored = false;
        DisplayQualityService.ApplyFrameLimits(Exe, new(144, null));
        profile.Settings[DisplayQualityService.FrameRateLimitId] = 165;
        Assert.Throws<InvalidOperationException>(() => DisplayQualityService.RestoreFrameLimits(Exe));
        Assert.Equal(165u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
    }
    [Fact]
    public void Failed_second_setting_never_commits_first_and_full_restore_can_consume_new_and_old_ids()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.FrameRateLimitId] = 60;
        _api.FailSettingId = DisplayQualityService.BackgroundFrameRateLimitId;
        Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyFrameLimits(Exe, new(144, 30)));
        Assert.Equal(0, _api.SaveCount); Assert.Equal(60u, Assert.Single(profile.Settings).Value);
        _api.FailSettingId = null;
        DisplayQualityService.ApplyFrameLimits(Exe, new(1000, 200));
        DisplayQualityService.ApplyPowerMode(Exe, PowerMode.OptimalPower);
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Equal(new KeyValuePair<uint, uint>(DisplayQualityService.FrameRateLimitId, 60), Assert.Single(profile.Settings));
    }
}
