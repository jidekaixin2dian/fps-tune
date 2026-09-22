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
/// 显示与画质服务：驱动层的按游戏设置（当前为 DLSS SR 预设覆盖）。
/// 覆盖写在"登记了该游戏 exe 的 profile"上（多为 NVIDIA 预置的游戏 profile，与
/// NVIDIA App 同一机制）；写之前把原值备份到本地 JSON，还原时恢复原值或删除设置。
/// 若游戏尚未登记在任何 profile，才创建本工具自建（"FpsTune · " 前缀）的 profile。
/// </summary>
public static class DisplayQualityService
{
    // 官方 NvApiDriverSettings.h
    public const uint DlssSrEnableId = 0x10E41E01; // NGX_DLSS_SR_OVERRIDE_ID
    public const uint DlssSrPresetId = 0x10E41DF3; // NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION_ID

    internal const string ProfilePrefix = "FpsTune · ";
    private static readonly uint[] ManagedSettingIds = { DlssSrEnableId, DlssSrPresetId };

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

    /// <summary>是否存在本工具写入前的原值备份（决定"还原"能否恢复原状态）。</summary>
    public static bool HasRestorableBackup(string gameExe) => ReadBackup(gameExe) is not null;

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

        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        using var session = api.OpenSession();

        var owner = session.FindApplicationOwner(gameExe);
        var isFirstWrite = ReadBackup(gameExe) is null;
        var ownProfile = owner is null;
        if (ownProfile)
        {
            owner = session.CreateProfile(ProfilePrefix + gameExe, gameExe);
        }
        // FindProfileByName 的句柄在本机驱动上不可与 GetSetting 组合（AV），还原时
        // 按 exe 重新定位 profile，因此自建/预置统一由 OwnProfile 标记区分

        // 先只在内存里记下原值；Save 成功后才落盘备份——否则 Save 被拒时会留下假备份
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

        session.SetSettingDword(owner, DlssSrEnableId, 1);
        session.SetSettingDword(owner, DlssSrPresetId, (uint)preset);
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

    /// <summary>
    /// 移除本工具的覆盖：按备份恢复原值（原来有值就写回，没有就删除设置项）；
    /// 覆盖写在自建 profile 时则整个删除该 profile。返回是否有覆盖被移除。
    /// </summary>
    public static bool RemoveDlssOverride(string gameExe)
    {
        var backup = ReadBackup(gameExe);
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        using var session = api.OpenSession();

        if (backup is not null)
        {
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
        return false;
    }
}
