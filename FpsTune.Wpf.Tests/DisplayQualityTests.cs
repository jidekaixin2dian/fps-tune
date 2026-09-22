using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 显示与画质服务（NVAPI DRS）逻辑测试：全部走内存态假实现，不触碰真实驱动。
/// 架构语义：覆盖写在"登记了游戏 exe 的 profile"上（与 NVIDIA App 同机制）；
/// 首次写入前必须备份原值；还原 = 恢复原值/删除设置，绝不碰无关 profile。
/// </summary>
[Collection("BackupService serial")]
public class DisplayQualityTests : IDisposable
{
    private const string Exe = "DeltaForceClient-Win64-Shipping.exe";

    private readonly FakeNvdrsApi _api = new();
    private readonly string _backupDir;

    public DisplayQualityTests()
    {
        _backupDir = Path.Combine(Path.GetTempPath(), "fpstune-dq-tests-" + Guid.NewGuid().ToString("N"));
        DisplayQualityService.ApiOverride = () => _api;
        DisplayQualityService.BackupDirOverride = _backupDir;
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        DisplayQualityService.ApiOverride = null;
        DisplayQualityService.BackupDirOverride = null;
        if (Directory.Exists(_backupDir))
            Directory.Delete(_backupDir, recursive: true);
    }

    [Fact]
    public void Apply_to_predefined_profile_writes_enable_and_preset_and_backs_up()
    {
        // NVIDIA 预置 profile：登记了游戏但没有 DLSS 覆盖设置
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);

        var predefined = _api.GetProfile("三角洲行动");
        Assert.NotNull(predefined);
        Assert.Equal(1u, predefined.Settings[DisplayQualityService.DlssSrEnableId]);
        Assert.Equal(11u, predefined.Settings[DisplayQualityService.DlssSrPresetId]);
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));

        var state = DisplayQualityService.GetDlssState(Exe);
        Assert.True(state.Covered);
        Assert.Equal(11u, state.PresetValue);
        Assert.True(state.Restorable);
    }

    [Fact]
    public void Restore_after_apply_recovers_predefined_profile_original_state()
    {
        _api.AddProfile("三角洲行动", Exe);
        // 预置 profile 上原本已有一条 DLSS 启用设置（值为 0）——备份必须记住它
        _api.GetProfile("三角洲行动")!.Settings[DisplayQualityService.DlssSrEnableId] = 0;

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetM);
        Assert.Equal(1u, _api.GetProfile("三角洲行动")!.Settings[DisplayQualityService.DlssSrEnableId]);

        DisplayQualityService.RemoveDlssOverride(Exe);

        // 原来有值的设置恢复原值；原来没有的设置被删除
        var settings = _api.GetProfile("三角洲行动")!.Settings;
        Assert.Equal(0u, settings[DisplayQualityService.DlssSrEnableId]);
        Assert.False(settings.ContainsKey(DisplayQualityService.DlssSrPresetId));
        Assert.False(DisplayQualityService.HasRestorableBackup(Exe));
    }

    [Fact]
    public void Apply_creates_own_profile_when_game_is_not_registered_anywhere()
    {
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetE);

        var own = _api.GetProfile("FpsTune · " + Exe);
        Assert.NotNull(own);
        Assert.Equal(5u, own.Settings[DisplayQualityService.DlssSrPresetId]);

        var state = DisplayQualityService.GetDlssState(Exe);
        Assert.True(state.Covered);

        // 还原：自建 profile 整体删除
        DisplayQualityService.RemoveDlssOverride(Exe);
        Assert.Null(_api.GetProfile("FpsTune · " + Exe));
    }

    [Fact]
    public void Second_apply_updates_values_without_overwriting_first_backup()
    {
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetJ);

        // 还原必须回到"从未覆盖过"的原点（无 K/J 残留），而不是上一次的 K
        DisplayQualityService.RemoveDlssOverride(Exe);
        var settings = _api.GetProfile("三角洲行动")!.Settings;
        Assert.False(settings.ContainsKey(DisplayQualityService.DlssSrEnableId));
        Assert.False(settings.ContainsKey(DisplayQualityService.DlssSrPresetId));
    }

    [Fact]
    public void Apply_followgame_is_honest_noop_when_nothing_to_remove()
    {
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.FollowGame);
        Assert.False(DisplayQualityService.HasRestorableBackup(Exe));
    }

    [Fact]
    public void Unsupported_driver_is_reported_honestly()
    {
        _api.SimulateMissingDriver = true;
        Assert.False(DisplayQualityService.IsNvidiaSupported);
        Assert.Throws<NvdrsException>(() =>
            DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK));
    }

    [Fact]
    public void Failed_save_does_not_leave_fake_backup()
    {
        _api.AddProfile("三角洲行动", Exe);
        _api.SimulateSaveDenied = true;

        Assert.Throws<NvdrsException>(() =>
            DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK));
        Assert.False(DisplayQualityService.HasRestorableBackup(Exe));

        // 保存失败后允许重试成功，并在成功后才出现备份
        _api.SimulateSaveDenied = false;
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
    }

    [Fact]
    public void Save_denied_error_mentions_admin_rights()
    {
        _api.AddProfile("三角洲行动", Exe);
        _api.SimulateSaveDenied = true;

        var ex = Assert.Throws<NvdrsException>(() =>
            DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK));
        Assert.Equal(-175, ex.Status);
        Assert.Contains("管理员", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>内存态 DRS：profile 集合 + 游戏 exe 登记表 + 设置字典。</summary>
    // ---------- M3 二期：纹理质量 / 电源 / 透明度 AA / 预渲染帧 ----------

    [Fact]
    public void M3_texture_quality_writes_official_setting_and_is_readable()
    {
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyTextureQuality(Exe, TextureFilterQuality.Performance);

        Assert.Equal(0x0au, _api.GetProfile("三角洲行动")!.Settings[DisplayQualityService.TextureQualityId]);
        var s = DisplayQualityService.GetDrsGameSettings(Exe);
        Assert.Equal(TextureFilterQuality.Performance, s.TextureQuality);
        Assert.True(s.Restorable);
    }

    [Fact]
    public void M3_power_mode_prefer_max_writes_official_pstate()
    {
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyPowerMode(Exe, PowerMode.PreferMax);

        Assert.Equal(1u, _api.GetProfile("三角洲行动")!.Settings[DisplayQualityService.PowerModeId]);
        Assert.Equal(PowerMode.PreferMax, DisplayQualityService.GetDrsGameSettings(Exe).PowerMode);
    }

    [Fact]
    public void M3_transparency_aa_supersample_uses_replay_and_clears_multisample()
    {
        _api.AddProfile("三角洲行动", Exe);
        _api.GetProfile("三角洲行动")!.Settings[DisplayQualityService.TransparencyMultisampleId] = 4;

        DisplayQualityService.ApplyTransparencyAa(Exe, TransparencyAa.Supersample2x);

        var settings = _api.GetProfile("三角洲行动")!.Settings;
        Assert.Equal(DisplayQualityService.TransparencySupersample2x, settings[DisplayQualityService.TransparencySupersampleId]);
        Assert.False(settings.ContainsKey(DisplayQualityService.TransparencyMultisampleId));
        Assert.Equal(TransparencyAa.Supersample2x, DisplayQualityService.GetDrsGameSettings(Exe).TransparencyAa);
    }

    [Fact]
    public void M3_transparency_aa_off_deletes_both_settings()
    {
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyTransparencyAa(Exe, TransparencyAa.Supersample2x);
        DisplayQualityService.ApplyTransparencyAa(Exe, TransparencyAa.Off);

        var settings = _api.GetProfile("三角洲行动")!.Settings;
        // 显式关闭 = 写 0；删除（未覆盖）只发生在还原时
        Assert.Equal(0u, settings[DisplayQualityService.TransparencyMultisampleId]);
        Assert.Equal(0u, settings[DisplayQualityService.TransparencySupersampleId]);
        Assert.Equal(TransparencyAa.Off, DisplayQualityService.GetDrsGameSettings(Exe).TransparencyAa);
    }

    [Fact]
    public void M3_pre_render_limit_writes_prerenderlimit_and_null_clears()
    {
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyPreRenderLimit(Exe, 1);
        Assert.Equal(1u, _api.GetProfile("三角洲行动")!.Settings[DisplayQualityService.PreRenderLimitId]);
        Assert.Equal(1u, DisplayQualityService.GetDrsGameSettings(Exe).PreRenderLimit);

        DisplayQualityService.ApplyPreRenderLimit(Exe, null);
        Assert.False(_api.GetProfile("三角洲行动")!.Settings.ContainsKey(DisplayQualityService.PreRenderLimitId));
        Assert.Null(DisplayQualityService.GetDrsGameSettings(Exe).PreRenderLimit);
    }

    [Fact]
    public void M3_and_dlss_share_one_backup_and_restore_recovers_all()
    {
        _api.AddProfile("三角洲行动", Exe);
        _api.GetProfile("三角洲行动")!.Settings[DisplayQualityService.TextureQualityId] = 0; // 原值

        DisplayQualityService.ApplyTextureQuality(Exe, TextureFilterQuality.HighPerformance);
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        DisplayQualityService.ApplyPowerMode(Exe, PowerMode.PreferMax);

        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
        DisplayQualityService.RemoveDlssOverride(Exe);

        var settings = _api.GetProfile("三角洲行动")!.Settings;
        // 原值写回；原本没有的设置被删
        Assert.Equal(0u, settings[DisplayQualityService.TextureQualityId]);
        Assert.False(settings.ContainsKey(DisplayQualityService.DlssSrPresetId));
        Assert.False(settings.ContainsKey(DisplayQualityService.PowerModeId));
        Assert.False(DisplayQualityService.HasRestorableBackup(Exe));
    }

    [Fact]
    public void M3_competitive_preset_writes_recommended_gear()
    {
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyCompetitivePreset(Exe, desktopHighEndGpu: false);

        var s = DisplayQualityService.GetDrsGameSettings(Exe);
        Assert.Equal(TextureFilterQuality.HighQuality, s.TextureQuality);
        Assert.Equal(PowerMode.PreferMax, s.PowerMode);
        Assert.Equal(TransparencyAa.Supersample2x, s.TransparencyAa);
        Assert.Equal(1u, s.PreRenderLimit);
        Assert.True(s.Restorable);
    }

    [Fact]
    public void M3_competitive_preset_uses_4x_transparency_on_desktop_high_end()
    {
        _api.AddProfile("三角洲行动", Exe);

        DisplayQualityService.ApplyCompetitivePreset(Exe, desktopHighEndGpu: true);

        var s = DisplayQualityService.GetDrsGameSettings(Exe);
        Assert.Equal(TransparencyAa.Supersample4x, s.TransparencyAa);
    }

    private sealed class FakeNvdrsApi : INvdrsApi
    {
        private readonly Dictionary<string, FakeProfile> _profiles = new();
        private readonly Dictionary<string, string> _exeOwners = new(); // exe -> profileName

        public bool SaveCalled { get; private set; }
        public bool SimulateMissingDriver { get; set; }
        public bool SimulateSaveDenied { get; set; }
        public string? LastError { get; private set; }

        internal FakeProfile AddProfile(string name, string? gameExe = null)
        {
            if (!_profiles.TryGetValue(name, out var profile))
            {
                profile = new FakeProfile { Name = name };
                _profiles[name] = profile;
            }
            if (gameExe is not null)
                _exeOwners[gameExe] = name;
            return profile;
        }

        internal FakeProfile? GetProfile(string name)
            => _profiles.TryGetValue(name, out var profile) ? profile : null;

        public bool TryInitialize()
        {
            if (SimulateMissingDriver)
                LastError = "未找到 NVIDIA 驱动库";
            return !SimulateMissingDriver;
        }

        public INvdrsSession OpenSession() => new FakeSession(this);

        public void Dispose() { }

        private sealed class FakeSession(FakeNvdrsApi api) : INvdrsSession
        {
            public void Dispose() { }

            public INvdrsProfile? FindApplicationOwner(string exeName)
                => api._exeOwners.TryGetValue(exeName, out var owner) && api.GetProfile(owner) is { } profile
                    ? new FakeProfileRef(profile)
                    : null;

            public INvdrsProfile CreateProfile(string name, string gameExe)
            {
                if (!name.StartsWith(DisplayQualityService.ProfilePrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("只允许创建 FpsTune 前缀的配置文件");
                if (!gameExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("应用必须是游戏主程序 exe");
                var profile = api.AddProfile(name);
                api._exeOwners[gameExe] = name;
                return new FakeProfileRef(profile);
            }

            public string GetProfileName(INvdrsProfile profile) => throw new NotSupportedException();

            public bool TryGetSettingDword(INvdrsProfile profile, uint settingId, out uint value)
                => ((FakeProfileRef)profile).Profile.Settings.TryGetValue(settingId, out value);

            public void SetSettingDword(INvdrsProfile profile, uint settingId, uint value)
                => ((FakeProfileRef)profile).Profile.Settings[settingId] = value;

            public bool DeleteSetting(INvdrsProfile profile, uint settingId)
                => ((FakeProfileRef)profile).Profile.Settings.Remove(settingId);

            public void DeleteProfile(INvdrsProfile profile)
            {
                var p = ((FakeProfileRef)profile).Profile;
                api._profiles.Remove(p.Name);
                foreach (var key in api._exeOwners.Where(kv => kv.Value == p.Name).Select(kv => kv.Key).ToList())
                    api._exeOwners.Remove(key);
            }

            public void Save()
            {
                if (api.SimulateSaveDenied)
                    throw new NvdrsException(-175, "保存驱动设置失败：NVAPI_ACCESS_DENIED（NVAPI -175）");
                api.SaveCalled = true;
            }
        }

        internal sealed class FakeProfile
        {
            public string Name { get; init; } = "";
            internal Dictionary<uint, uint> Settings { get; } = new();
        }

        private sealed class FakeProfileRef(FakeProfile profile) : INvdrsProfile
        {
            public FakeProfile Profile { get; } = profile;
            public IntPtr Handle { get; } = (IntPtr)profile.Name.GetHashCode();
        }
    }
}
