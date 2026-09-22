using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>
/// DLSS Super Resolution 预设（枚举值来自官方 NvApiDriverSettings.h 的
/// NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION）。FollowGame 表示移除本工具的覆盖。
/// </summary>
public enum DlssPreset
{
    FollowGame = 0,       // 跟随游戏内设置
    Latest = 0x00ffffff,  // 官方"最新"预设
    PresetE = 5,          // 旧一代 CNN 模型
    PresetJ = 10,
    PresetK = 11,         // 社区常用的新一代模型预设（三角洲教学点名"K 模型"）
    PresetM = 13,         // 新一代模型的高端预设（用户指定为高端首选）
}

/// <summary>
/// 纹理过滤 - 质量（官方 QUALITY_ENHANCEMENTS，显示名 "Texture filtering - Quality"）。
/// </summary>
public enum TextureFilterQuality : uint
{
    Quality = 0x00000000,
    Performance = 0x0000000a,
    HighPerformance = 0x00000014,
    HighQuality = 0xfffffff6,
}

/// <summary>
/// 电源管理模式（官方 PREFERRED_PSTATE，显示名 "Power management mode"）。
/// </summary>
public enum PowerMode : uint
{
    Adaptive = 0x0,
    PreferMax = 0x1,
    DriverControlled = 0x2,
    PreferConsistentPerformance = 0x3,
    PreferMin = 0x4,
    OptimalPower = 0x5,
}

/// <summary>
/// 平滑处理 - 透明度。官方拆成两项：
/// 多重采样 = AA_MODE_ALPHATOCOVERAGE（Transparency Multisampling）；
/// 超级采样 = AA_MODE_REPLAY（Transparency Supersampling）：2x=SAMPLES_TWO|0x03，4x=AA_MODE_REPLAY_TRANSPARENCY。
/// 推荐：默认 2x（画质/开销平衡）；桌面非笔电高端卡（如 5070 Ti）才推 4x。
/// </summary>
public enum TransparencyAa
{
    Off = 0,
    Multisample = 1,
    Supersample2x = 2,
    Supersample4x = 3,
}

/// <summary>各向异性过滤倍数（官方 ANISO_MODE_LEVEL；0/1=关，0x10=16x）。推荐 16x：现代卡开销极低。</summary>
public enum AnisoLevel : uint
{
    AppControlled = 0xffffffff,
    Off = 0x00000000,
    Level2 = 0x00000002,
    Level4 = 0x00000004,
    Level8 = 0x00000008,
    Level16 = 0x00000010,
}

/// <summary>垂直同步（官方 VSYNCMODE）。竞技推荐强制关。</summary>
public enum VSyncMode
{
    AppControlled = 0,
    ForceOff = 1,
    ForceOn = 2,
}

/// <summary>
/// 显示与画质服务：驱动层的按游戏设置（DLSS SR 预设覆盖 + M3 二期 3D 设置）。
/// 覆盖写在"登记了该游戏 exe 的 profile"上（多为 NVIDIA 预置的游戏 profile，与
/// NVIDIA App 同一机制）；写之前把原值备份到本地 JSON，还原时恢复原值或删除设置。
/// 若游戏尚未登记在任何 profile，才创建本工具自建（"FpsTune · " 前缀）的 profile。
/// SettingID / 枚举值均来自 NVIDIA 官方 NvApiDriverSettings.h（NVIDIA/nvapi）。
/// </summary>
public static class DisplayQualityService
{
    // 官方 NvApiDriverSettings.h
    public const uint DlssSrEnableId = 0x10E41E01; // NGX_DLSS_SR_OVERRIDE_ID
    public const uint DlssSrPresetId = 0x10E41DF3; // NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION_ID
    public const uint TextureQualityId = 0x00CE2691; // QUALITY_ENHANCEMENTS_ID
    public const uint PowerModeId = 0x1057EB71; // PREFERRED_PSTATE_ID
    public const uint TransparencyMultisampleId = 0x10FC2D9C; // AA_MODE_ALPHATOCOVERAGE_ID
    public const uint TransparencySupersampleId = 0x10D48A85; // AA_MODE_REPLAY_ID
    public const uint PreRenderLimitId = 0x007BA09E; // PRERENDERLIMIT_ID
    // P2-6 热门面板项（官方 NvApiDriverSettings.h）
    public const uint AnisoSelectorId = 0x10D2BB16; // ANISO_MODE_SELECTOR_ID
    public const uint AnisoLevelId = 0x101E61A9; // ANISO_MODE_LEVEL_ID
    public const uint VSyncModeId = 0x00A879CF; // VSYNCMODE_ID
    public const uint ShaderDiskCacheId = 0x00198FFF; // PS_SHADERDISKCACHE_ID

    public const uint AnisoSelectorUser = 0x1;
    public const uint AnisoLevel16x = 0x10;
    public const uint VSyncForceOff = 0x08416747;
    public const uint ShaderCacheOn = 0x1;

    /// <summary>官方 AA_MODE_REPLAY_TRANSPARENCY（4x 超级采样透明度）= SAMPLES_FOUR|0x03。</summary>
    public const uint TransparencySupersample4x = 0x00000023;
    /// <summary>2x 超级采样透明度 = SAMPLES_TWO|0x03（与官方 TRANSPARENCY 同 mode 位）。</summary>
    public const uint TransparencySupersample2x = 0x00000013;
    public const uint TransparencyMultisampleOn = 0x00000004;

    internal const string ProfilePrefix = "FpsTune · ";
    private static readonly uint[] ManagedSettingIds =
    {
        DlssSrEnableId, DlssSrPresetId,
        TextureQualityId, PowerModeId,
        TransparencyMultisampleId, TransparencySupersampleId,
        PreRenderLimitId,
        AnisoSelectorId, AnisoLevelId, VSyncModeId, ShaderDiskCacheId,
    };

    /// <summary>
    /// DLSS 覆盖功能开关。曾经因 profile 句柄被二次解引用（ReadIntPtr）导致 GetSetting AV 而停用；
    /// 2026-09-22 按 nvidiaProfileInspector 句柄语义修复后默认启用。
    /// 环境变量 FPS_ENABLE_DLSS=0 可在验证时临时关掉。
    /// </summary>
    public static bool FeatureEnabled => Environment.GetEnvironmentVariable("FPS_ENABLE_DLSS") != "0";

    // 测试注入：null 时使用真实 NVAPI
    internal static Func<INvdrsApi>? ApiOverride;
    internal static string? BackupDirOverride;

    private static INvdrsApi CreateApi() => ApiOverride?.Invoke() ?? NvdrsApi.Shared;

    private static string BackupDir()
        => BackupDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "display-quality");

    /// <summary>本机是否有可用的 NVIDIA 驱动（NVAPI 初始化成功）。</summary>
    public static bool IsNvidiaSupported
    {
        get
        {
            using var api = CreateApi();
            return api.TryInitialize();
        }
    }

    /// <summary>
    /// DLSS 覆盖状态。Covered=false 表示未覆盖；Covered=true 时 IsOurs 区分
    /// 本工具的覆盖与其他工具/NVIDIA App 已有的覆盖；Restorable 表示存在可还原的备份。
    /// </summary>
    public sealed record DlssState(string GameExe, bool Covered, uint? PresetValue, bool Restorable);

    public static DlssState GetDlssState(string gameExe)
    {
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        using var session = api.OpenSession();
        var restorable = ReadBackup(gameExe) is not null;
        var owner = session.FindApplicationOwner(gameExe);
        if (owner is null)
            return new DlssState(gameExe, false, null, restorable);
        return session.TryGetSettingDword(owner, DlssSrPresetId, out var preset)
            ? new DlssState(gameExe, true, preset, restorable)
            : new DlssState(gameExe, false, null, restorable);
    }

    /// <summary>M3 二期：按游戏的驱动 3D 设置快照。null 表示该项未覆盖（跟随驱动/游戏默认）。</summary>
    public sealed record DrsGameSettings(
        string GameExe,
        TextureFilterQuality? TextureQuality,
        PowerMode? PowerMode,
        TransparencyAa? TransparencyAa,
        uint? PreRenderLimit,
        bool Restorable,
        AnisoLevel? Aniso = null,
        VSyncMode? VSync = null,
        bool? ShaderCache = null);

    /// <summary>各向异性过滤。AppControlled=删除设置；16x 为社区/文档推荐（现代卡开销极低）。</summary>
    public static void ApplyAnisoLevel(string gameExe, AnisoLevel level)
        => ApplyManaged(gameExe, (session, owner) =>
        {
            if (level == AnisoLevel.AppControlled)
            {
                session.DeleteSetting(owner, AnisoSelectorId);
                session.DeleteSetting(owner, AnisoLevelId);
                return;
            }
            session.SetSettingDword(owner, AnisoSelectorId, AnisoSelectorUser);
            session.SetSettingDword(owner, AnisoLevelId, (uint)level);
        });

    /// <summary>垂直同步。竞技推荐 ForceOff；三重缓冲仅 OGL 有独立项，D3D 下与 VSync 一并关闭即可。</summary>
    public static void ApplyVSyncMode(string gameExe, VSyncMode mode)
        => ApplyManaged(gameExe, (session, owner) =>
        {
            if (mode == VSyncMode.AppControlled)
            {
                session.DeleteSetting(owner, VSyncModeId);
                return;
            }
            session.SetSettingDword(
                owner, VSyncModeId, mode == VSyncMode.ForceOn ? 0x47814940u : VSyncForceOff);
        });

    /// <summary>着色器磁盘缓存。null=删除；true=开（推荐，减少运行时编译卡顿）。</summary>
    public static void ApplyShaderDiskCache(string gameExe, bool? enabled)
        => ApplyManaged(gameExe, (session, owner) =>
        {
            if (enabled is null)
                session.DeleteSetting(owner, ShaderDiskCacheId);
            else
                session.SetSettingDword(owner, ShaderDiskCacheId, enabled.Value ? ShaderCacheOn : 0u);
        });

    /// <summary>
    /// 社区/教学高频「竞技 3D 预设」（2026-09-22 调研）：
    /// 纹理高质量 + 电源最高性能优先 + 透明度 2x + 预渲染 1 帧 + AF16x + VSync 关 + 着色器缓存开。
    /// <paramref name="desktopHighEndGpu"/> 为 true（桌面非笔电高端卡，如 5070 Ti）时透明度升到 4x。
    /// </summary>
    public static void ApplyCompetitivePreset(string gameExe, bool desktopHighEndGpu)
    {
        ApplyTextureQuality(gameExe, TextureFilterQuality.HighQuality);
        ApplyPowerMode(gameExe, PowerMode.PreferMax);
        ApplyTransparencyAa(gameExe, desktopHighEndGpu ? TransparencyAa.Supersample4x : TransparencyAa.Supersample2x);
        ApplyPreRenderLimit(gameExe, 1);
        ApplyAnisoLevel(gameExe, AnisoLevel.Level16);
        ApplyVSyncMode(gameExe, VSyncMode.ForceOff);
        ApplyShaderDiskCache(gameExe, true);
    }

    public static DrsGameSettings GetDrsGameSettings(string gameExe)
    {
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        using var session = api.OpenSession();
        var restorable = ReadBackup(gameExe) is not null;
        var owner = session.FindApplicationOwner(gameExe);
        if (owner is null)
            return new DrsGameSettings(gameExe, null, null, null, null, restorable);

        TextureFilterQuality? tex = session.TryGetSettingDword(owner, TextureQualityId, out var t)
            ? (TextureFilterQuality)t : null;
        PowerMode? power = session.TryGetSettingDword(owner, PowerModeId, out var p)
            ? (PowerMode)p : null;
        TransparencyAa? traa = null;
        var hasMulti = session.TryGetSettingDword(owner, TransparencyMultisampleId, out var multi);
        var hasSuper = session.TryGetSettingDword(owner, TransparencySupersampleId, out var super);
        if (hasSuper && (super & 0x0f) != 0)
        {
            var samples = super & 0x70;
            traa = samples >= 0x20 ? TransparencyAa.Supersample4x : TransparencyAa.Supersample2x;
        }
        else if (hasMulti && multi != 0)
            traa = TransparencyAa.Multisample;
        else if (hasMulti || hasSuper)
            traa = TransparencyAa.Off;
        uint? prerender = session.TryGetSettingDword(owner, PreRenderLimitId, out var pr) && pr != 0
            ? pr : null;
        AnisoLevel? aniso = null;
        if (session.TryGetSettingDword(owner, AnisoLevelId, out var af))
            aniso = (AnisoLevel)af;
        VSyncMode? vsync = null;
        if (session.TryGetSettingDword(owner, VSyncModeId, out var vs))
        {
            vsync = vs switch
            {
                VSyncForceOff => VSyncMode.ForceOff,
                0x47814940u => VSyncMode.ForceOn,
                _ => VSyncMode.AppControlled,
            };
        }
        bool? shaderCache = session.TryGetSettingDword(owner, ShaderDiskCacheId, out var sc)
            ? sc != 0 : null;
        return new DrsGameSettings(gameExe, tex, power, traa, prerender, restorable, aniso, vsync, shaderCache);
    }

    /// <summary>存在本工具写入前的原值备份（任意受管设置的可还原快照）。</summary>
    public static bool HasRestorableBackup(string gameExe) => ReadBackup(gameExe) is not null;

    /// <summary>纹理过滤 - 质量。</summary>
    public static void ApplyTextureQuality(string gameExe, TextureFilterQuality quality)
        => ApplyManaged(gameExe, (session, owner) =>
            session.SetSettingDword(owner, TextureQualityId, (uint)quality));

    /// <summary>电源管理模式。PreferMax = 最高性能优先。</summary>
    public static void ApplyPowerMode(string gameExe, PowerMode mode)
        => ApplyManaged(gameExe, (session, owner) =>
            session.SetSettingDword(owner, PowerModeId, (uint)mode));

    /// <summary>
    /// 平滑处理 - 透明度（关闭 / 多重采样 / 超级采样 2x 推荐 / 超级采样 4x 桌面高端卡）。
    /// </summary>
    public static void ApplyTransparencyAa(string gameExe, TransparencyAa mode)
        => ApplyManaged(gameExe, (session, owner) =>
        {
            if (mode == TransparencyAa.Off)
            {
                // 显式关闭：写 0（官方 MODE_OFF），区别于「未覆盖/跟随驱动」（设置不存在）
                session.SetSettingDword(owner, TransparencyMultisampleId, 0);
                session.SetSettingDword(owner, TransparencySupersampleId, 0);
                return;
            }
            if (mode == TransparencyAa.Multisample)
            {
                session.SetSettingDword(owner, TransparencyMultisampleId, TransparencyMultisampleOn);
                session.DeleteSetting(owner, TransparencySupersampleId);
                return;
            }
            session.DeleteSetting(owner, TransparencyMultisampleId);
            session.SetSettingDword(
                owner,
                TransparencySupersampleId,
                mode == TransparencyAa.Supersample4x ? TransparencySupersample4x : TransparencySupersample2x);
        });

    /// <summary>
    /// 低延迟 · 最大预渲染帧数（官方 PRERENDERLIMIT）。
    /// 评估（2026-09-22）：竞技 FPS 推荐 1 帧（≈驱动「低延迟模式：开启」的队列语义）；
    /// CPU 偏弱可 2；若游戏内置 NVIDIA Reflex，优先游戏内 Reflex + 应用程序控制。
    /// null = 应用程序控制（还原/跟随语义）。
    /// </summary>
    public static void ApplyPreRenderLimit(string gameExe, uint? frames)
        => ApplyManaged(gameExe, (session, owner) =>
        {
            if (frames is null or 0)
                session.DeleteSetting(owner, PreRenderLimitId);
            else
                session.SetSettingDword(owner, PreRenderLimitId, frames.Value);
        });

    /// <summary>写入一项受管设置：首次写入前备份全部受管项；Save 成功才落盘备份。</summary>
    private static void ApplyManaged(string gameExe, Action<INvdrsSession, INvdrsProfile> write)
    {
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        using var session = api.OpenSession();

        var owner = session.FindApplicationOwner(gameExe);
        var isFirstWrite = ReadBackup(gameExe) is null;
        var ownProfile = owner is null;
        if (ownProfile)
            owner = session.CreateProfile(ProfilePrefix + gameExe, gameExe);

        List<SettingBackup>? pendingBackup = null;
        if (isFirstWrite)
        {
            pendingBackup = new List<SettingBackup>();
            foreach (var id in ManagedSettingIds)
            {
                if (session.TryGetSettingDword(owner, id, out var current))
                    pendingBackup.Add(new SettingBackup(id, true, current));
                else
                    pendingBackup.Add(new SettingBackup(id, false, 0));
            }
        }

        write(session, owner);
        try
        {
            session.Save();
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            throw new NvdrsException(-175,
                "保存驱动设置被拒绝（NVAPI_ACCESS_DENIED）：写入 NVIDIA 配置需要管理员权限。" +
                "请以管理员身份重启本程序后再应用。");
        }
        if (pendingBackup is not null)
            WriteBackup(new OverrideBackup(gameExe, ownProfile, pendingBackup));
    }

    private sealed record SettingBackup(uint SettingId, bool Existed, uint Value);
    private sealed record OverrideBackup(string GameExe, bool OwnProfile, List<SettingBackup> Settings);

    private static string BackupPath(string gameExe)
        => Path.Combine(BackupDir(), "backup-" + gameExe + ".json");

    private static OverrideBackup? ReadBackup(string gameExe)
    {
        var path = BackupPath(gameExe);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<OverrideBackup>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteBackup(OverrideBackup backup)
    {
        Directory.CreateDirectory(BackupDir());
        AtomicFile.WriteAllText(
            BackupPath(backup.GameExe),
            JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
    }

    private static void DeleteBackup(string gameExe)
    {
        var path = BackupPath(gameExe);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>应用 DLSS 预设覆盖；FollowGame 等价于移除覆盖。</summary>
    public static void ApplyDlssPreset(string gameExe, DlssPreset preset)
    {
        if (preset == DlssPreset.FollowGame)
        {
            RemoveDlssOverride(gameExe);
            return;
        }

        ApplyManaged(gameExe, (session, owner) =>
        {
            session.SetSettingDword(owner, DlssSrEnableId, 1);
            session.SetSettingDword(owner, DlssSrPresetId, (uint)preset);
        });
    }

    /// <summary>
    /// 移除本工具的覆盖：按备份恢复原值（原来有值就写回，没有就删除设置项）；
    /// 覆盖写在自建 profile 时则整个删除该 profile。返回是否有覆盖被移除。
    /// DLSS 与 M3 二期 3D 设置共用同一份备份与还原。
    /// </summary>
    public static bool RemoveDlssOverride(string gameExe)
    {
        var backup = ReadBackup(gameExe);
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        using var session = api.OpenSession();

        if (backup is null)
            return false;

        if (backup.OwnProfile)
        {
            // 自建 profile：本来就是我们为这次覆盖创建的，按 exe 定位后整体删除
            var target = session.FindApplicationOwner(gameExe);
            if (target is not null)
                session.DeleteProfile(target);
        }
        else
        {
            // 预置/用户 profile：按 exe 重新定位（登记关系持久存在），恢复写入前的原值
            var target = session.FindApplicationOwner(gameExe);
            if (target is not null)
            {
                foreach (var setting in backup.Settings)
                {
                    if (setting.Existed)
                        session.SetSettingDword(target, setting.SettingId, setting.Value);
                    else
                        session.DeleteSetting(target, setting.SettingId);
                }
            }
        }
        try
        {
            session.Save();
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            throw new NvdrsException(-175,
                "保存驱动设置被拒绝（NVAPI_ACCESS_DENIED）：还原 NVIDIA 配置需要管理员权限。" +
                "请以管理员身份重启本程序后再还原。");
        }
        DeleteBackup(gameExe);
        return true;
    }
}
