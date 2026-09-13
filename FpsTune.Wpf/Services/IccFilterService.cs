using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>
/// ICC 滤镜服务：程序生成预设 profile、安装到系统色彩目录并切换主显示器默认关联。
/// 安全语义（不可弱化）：
///  - 进入功能（首次切换）前按 DetectionService 颜色配置检查的同一读取口径，把当前关联列表
///    与生效默认 profile 持久化备份（JSON + AtomicFile 落盘到 %LOCALAPPDATA%\FpsTune\display-icc\）；
///  - 备份始终代表"本工具介入前"的原始状态；切换到其他预设不覆盖备份；
///  - 切换前发现显示器配置在本工具之外被修改（当前列表 ≠ 备份列表 + 本工具预设）→ 如实拒绝；
///  - 还原 = 按备份精确写回原关联列表并验证生效，成功后才删除备份；
///  - 第一版只作用主显示器，多显示器显式不支持。
/// </summary>
public static class IccFilterService
{
    // 测试注入：null 时使用真实 mscms/注册表实现
    internal static Func<IIccSystemApi>? ApiOverride;
    internal static string? BackupDirOverride;

    public static bool IsSupported
    {
        get
        {
            try
            {
                return CreateApi().TryGetPrimaryDisplay(out var display) && display.UsePerUserProfiles;
            }
            catch
            {
                return false;
            }
        }
    }

    private static IIccSystemApi CreateApi() => ApiOverride?.Invoke() ?? new MscmsIccApi();

    private static string BackupDir()
        => BackupDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "display-icc");

    private static string BackupPath() => Path.Combine(BackupDir(), "backup.json");

    private sealed record Backup(string AssociationSubKey, string DefaultProfile, string[] ListBefore, string CreatedAt);

    private static Backup? ReadBackup()
    {
        var path = BackupPath();
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Backup>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteBackup(Backup backup)
    {
        AtomicFile.WriteAllText(
            BackupPath(),
            JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
    }

    private static void DeleteBackup()
    {
        var path = BackupPath();
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary> ICC 滤镜当前状态。</summary>
    public sealed record IccState(
        bool Supported,
        string? UnsupportedReason,
        string? CurrentProfileName,
        string? CurrentProfilePath,
        bool Restorable);

    public static IccState GetState()
    {
        var api = CreateApi();
        if (!api.TryGetPrimaryDisplay(out var display))
            return new IccState(false, "无法定位主显示器（ICC 滤镜第一版只作用主显示器）。", null, null, HasBackup());
        if (!display.UsePerUserProfiles)
            return new IccState(false, "当前显示器使用系统级色彩关联，本版本暂不支持切换。", null, null, HasBackup());

        var colorDir = api.TryGetColorDirectory();
        if (colorDir is null)
            return new IccState(false, "无法读取系统色彩目录。", null, null, HasBackup());

        var current = EffectiveDefault(api, display, colorDir);
        return new IccState(
            true,
            null,
            current,
            current is null ? null : Path.Combine(colorDir, current),
            HasBackup());
    }

    /// <summary>应用预设。成功返回生效的 profile 文件名；失败抛异常，系统状态只可能停在已验证的位置。</summary>
    public static string Apply(IccFilterPreset preset)
    {
        var api = CreateApi();
        if (!api.TryGetPrimaryDisplay(out var display))
            throw new InvalidOperationException("无法定位主显示器，ICC 滤镜第一版只作用主显示器。");
        if (!display.UsePerUserProfiles)
            throw new InvalidOperationException("当前显示器使用系统级色彩关联，本版本暂不支持切换。");
        var colorDir = api.TryGetColorDirectory() ?? throw new InvalidOperationException("无法读取系统色彩目录。");

        var list = api.ReadAssociationList(display);
        var currentDefault = EffectiveDefault(api, display, colorDir);
        if (currentDefault is null)
            throw new InvalidOperationException(
                "未读取到当前生效的显示 profile，为避免无法还原，已拒绝切换。请先在 Windows 颜色管理中确认当前显示器有可用配置文件。");

        var backup = ReadBackup();
        if (backup is null)
        {
            WriteBackup(new Backup(display.AssociationSubKey, currentDefault, list, DateTime.Now.ToString("s")));
        }
        else
        {
            // 已有备份：只允许"备份列表 + 本工具预设追加"的漂移；之外的变化如实拒绝
            if (!IsBackupPlusOwnPresets(list, backup, colorDir))
                throw new InvalidOperationException(
                    "检测到显示器色彩配置在本工具之外被修改过。为避免覆盖你的设置，本次未切换；请先点「还原原始」，再重新应用预设。");
            if (backup.AssociationSubKey != display.AssociationSubKey)
                throw new InvalidOperationException(
                    "主显示器与备份时不一致，已拒绝切换。请先点「还原原始」后再试。");
        }

        string presetName = EnsurePresetInstalled(api, preset, colorDir);

        if (!api.SetDisplayDefaultAssociation(display, presetName))
            throw new InvalidOperationException("系统拒绝了 profile 切换请求（未生效）。");

        // 验证：读回关联列表，最后一个有效 profile 必须是刚设置的预设
        var after = api.ReadAssociationList(display);
        var verified = EffectiveDefault(api, display, colorDir);
        if (verified != presetName)
            throw new InvalidOperationException(
                $"切换后验证失败：当前生效 profile 仍是 {verified ?? "<无>"}，系统设置未达成。");

        return presetName;
    }

    /// <summary>还原为备份的原始 profile。返回是否执行了还原；无备份时返回 false（如实无事发生）。</summary>
    public static bool Restore()
    {
        var backup = ReadBackup();
        if (backup is null)
            return false;

        var api = CreateApi();
        if (!api.TryGetPrimaryDisplay(out var display))
            throw new InvalidOperationException("无法定位主显示器，无法还原。");
        if (display.AssociationSubKey != backup.AssociationSubKey)
            throw new InvalidOperationException("主显示器与备份时不一致，无法按备份精确还原。");

        if (!api.SetDisplayDefaultAssociation(display, backup.DefaultProfile))
            throw new InvalidOperationException("系统拒绝了还原请求（未生效），备份已保留，可重试。");

        // 精确还原：写回备份时的完整关联列表（同时清掉本工具追加的预设条目）
        api.WriteAssociationList(display, backup.ListBefore);

        var after = api.ReadAssociationList(display);
        var colorDir = api.TryGetColorDirectory();
        var verified = colorDir is null ? null : EffectiveDefault(api, display, colorDir);
        if (!after.SequenceEqual(backup.ListBefore) || verified != backup.DefaultProfile)
            throw new InvalidOperationException("还原后验证失败，备份已保留，可重试。");

        DeleteBackup();
        return true;
    }

    /// <summary>把预设 profile 写到本地并安装进系统色彩目录（幂等）。返回系统色彩目录内的文件名。</summary>
    private static string EnsurePresetInstalled(IIccSystemApi api, IccFilterPreset preset, string colorDir)
    {
        var @params = IccProfileGenerator.ParamsFor(preset);
        string installedPath = Path.Combine(colorDir, @params.FileName);
        if (IccProfileFile.IsDisplayProfile(installedPath))
            return @params.FileName;

        byte[] bytes = IccProfileGenerator.Build(@params);
        string localPath = Path.Combine(BackupDir(), "presets", @params.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        AtomicFile.WriteAllBytes(localPath, bytes);

        if (!api.InstallColorProfile(localPath))
            throw new InvalidOperationException("安装 profile 到系统色彩目录失败。");
        if (!IccProfileFile.IsDisplayProfile(installedPath))
            throw new InvalidOperationException("安装后未在系统色彩目录找到 profile。");
        return @params.FileName;
    }

    /// <summary>生效默认 = per-user 关联列表中从末尾数第一个有效显示类 profile。</summary>
    private static string? EffectiveDefault(IIccSystemApi api, IccDisplayInfo display, string colorDir)
    {
        foreach (var name in api.ReadAssociationList(display).Reverse())
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (IccProfileFile.IsDisplayProfile(Path.Combine(colorDir, name)))
                return name;
        }
        return null;
    }

    /// <summary>当前列表是否 = 备份列表 + 本工具预设名（顺序与数量漂移都算外部修改）。</summary>
    private static bool IsBackupPlusOwnPresets(string[] list, Backup backup, string colorDir)
    {
        var ownNames = AllPresetFileNames();
        var trimmed = new List<string>(list.Length);
        foreach (var n in list)
        {
            if (ownNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                continue;
            trimmed.Add(n);
        }
        return trimmed.SequenceEqual(backup.ListBefore, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>本工具生成的全部预设文件名（含尚未生成的）。</summary>
    internal static string[] AllPresetFileNames()
        => Enum.GetValues<IccFilterPreset>()
            .Select(p => IccProfileGenerator.ParamsFor(p).FileName)
            .ToArray();

    private static bool HasBackup() => ReadBackup() is not null;
}
