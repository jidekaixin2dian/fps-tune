using System.IO;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// ICC 滤镜服务：安装 profile 并切换选定显示器的默认关联。
/// 安全语义（不可弱化）：
///  - 进入功能（首次切换）前按 DetectionService 颜色配置检查的同一读取口径，把当前关联列表
///    与生效默认 profile 持久化备份（JSON + AtomicFile 落盘到 %LOCALAPPDATA%\FpsTune\display-icc\）；
///  - 正常切换预设保留首次原值；明确的手动重新应用遇到冲突时，归档旧备份并捕获当前恢复点；
///  - 自动操作及还原发现外部改动时拒绝覆盖；目标身份无法确认或备份损坏时始终拒绝；
///  - 还原 = 按备份精确写回原关联列表并验证生效，成功后才删除备份；
///  - 逐屏识别并保留原点；复制模式的共享 source 不允许逐屏修改。
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
        => BackupDirOverride ?? Path.Combine(UserDataPaths.Root, "display-icc");

    private static string BackupPath(string? identity = null) => DisplayBackupFiles.PathFor(BackupDir(), "backup", identity);

    private sealed record Backup(string AssociationSubKey, string? DefaultProfile, string[] ListBefore, string CreatedAt, string? ExpectedProfile = null, string? PreviousProfile = null, string? Identity = null, string? DisplayName = null);

    private static Backup ParseBackup(string json)
    {
        var backup = JsonSerializer.Deserialize<Backup>(json);
        if (backup is null || backup.ListBefore is null || backup.ListBefore.Length > 1000
            || string.IsNullOrWhiteSpace(backup.AssociationSubKey)
            || string.IsNullOrWhiteSpace(backup.DefaultProfile) && (backup.Identity is null || backup.ListBefore.Length != 0))
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        return backup;
    }

    internal static void ValidateTransferredBackup(string name, byte[] bytes)
    {
        var backup = ParseBackup(System.Text.Encoding.UTF8.GetString(bytes));
        if (name != Path.GetFileName(BackupPath(backup.Identity))
            || backup.ListBefore.Any(p => string.IsNullOrWhiteSpace(p) || p != Path.GetFileName(p)))
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
    }

    private static Backup? ReadBackup(string? identity = null)
    {
        var path = BackupPath(identity);
        if (!File.Exists(path))
            return null;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
            return ParseBackup(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            // 损坏的备份既不能当「无改动」让 Apply 拿滤镜态当基线，也不能永远卡死 Apply/Restore：
            // 留档 .corrupt 后清掉，按无备份处理（Apply 会用当前真实状态重建基线）。
            try { AtomicFile.PreserveCorrupt(path); } catch { }
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    private static void WriteBackup(Backup backup)
    {
        AtomicFile.WriteAllTextDurable(
            BackupPath(backup.Identity),
            JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
    }

    private static void DeleteBackup(string? identity)
    {
        var path = BackupPath(identity);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary> ICC 滤镜当前状态。</summary>
    public sealed record IccState(
        bool Supported,
        string? UnsupportedReason,
        string? CurrentProfileName,
        string? CurrentProfilePath,
        bool Restorable, bool CanApply = false, string? BackupNotice = null);

    public static IccState GetState(string? identity = null)
    {
        var api = CreateApi();
        IccDisplayInfo display;
        try { display = SelectDisplay(api, identity); }
        catch (Exception ex) { return new IccState(false, ex.Message, null, null, false); }
        if (!display.UsePerUserProfiles)
            return new IccState(false, "当前显示器使用系统级色彩关联，本版本暂不支持切换。", null, null, HasRestorableBackup(identity));

        var colorDir = api.TryGetColorDirectory();
        if (colorDir is null)
            return new IccState(false, "无法读取系统色彩目录。", null, null, HasRestorableBackup(identity));

        var current = EffectiveDefault(api, display, colorDir);
        var canApply = current is not null || api.CanRestoreAbsentDefault && api.ReadAssociationList(display).Length == 0;
        var backup = ReadForDisplay(display, forNewApply: true);
        var legacy = backup is null ? ReadBackup() : null;
        var changed = backup is not null && !IsCompatible(display, api.ReadAssociationList(display), current, backup, colorDir)
            || legacy is { Identity: null } && legacy.AssociationSubKey == display.AssociationSubKey;
        return new IccState(
            true,
            canApply ? null : "未读取到当前关联中的可用显示 profile。请先在 Windows 颜色管理中检查配置文件。",
            current,
            current is null ? null : Path.Combine(colorDir, current),
            backup is not null && backup.AssociationSubKey == display.AssociationSubKey,
            canApply, changed ? Str.T("Str.IccNewBaselineHint") : null);
    }

    /// <summary>本工具从用户文件安装的自定义 profile 统一用此前缀（漂移检查据此放行）。</summary>
    internal const string CustomProfilePrefix = "FpsTune-Custom-";

    internal static bool HasManagedPreset(string identity)
    {
        var api = CreateApi();
        var display = SelectDisplay(api, identity);
        var directory = api.TryGetColorDirectory();
        if (!display.UsePerUserProfiles || directory is null) return false;
        var current = EffectiveDefault(api, display, directory);
        return current is not null && AllPresetFileNames().Contains(current, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>应用预设。成功返回生效的 profile 文件名；失败抛异常，系统状态只可能停在已验证的位置。</summary>
    public static string Apply(IccFilterPreset preset, string? identity = null, bool startNewBaselineOnDrift = false)
    {
        using var gate = SystemMutationGate.Acquire();
        var api = CreateApi();
        var (display, colorDir) = RequireDisplayAndColorDir(api, identity);
        string presetName = EnsurePresetInstalled(api, preset, colorDir);
        return AssociateWithBackup(api, display, colorDir, presetName, startNewBaselineOnDrift);
    }

    /// <summary>
    /// 应用用户自选的 .icc / .icm 文件（P2-10）。与预设走同一套备份/漂移保护/验证，
    /// 自定义文件以内容哈希命名安装进系统色彩目录，可随时「还原原始」。
    /// </summary>
    public static string ApplyFromFile(string sourcePath, string? identity = null, bool startNewBaselineOnDrift = false)
    {
        using var gate = SystemMutationGate.Acquire();
        if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 16 * 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException(Str.T("Str.IccFileMissing"));
        var ext = Path.GetExtension(sourcePath);
        if (!ext.Equals(".icc", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".icm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Str.T("Str.IccFileExtUnsupported"));
        // 与预设同一条"可用显示 profile"判定口径：设备类 mntr + RGB 色彩空间
        if (!IccProfileFile.IsDisplayProfile(sourcePath))
            throw new InvalidOperationException(Str.T("Str.IccFileNotDisplay"));

        var api = CreateApi();
        var (display, colorDir) = RequireDisplayAndColorDir(api, identity);
        string name = EnsureCustomInstalled(api, sourcePath, colorDir);
        return AssociateWithBackup(api, display, colorDir, name, startNewBaselineOnDrift);
    }

    private static (IccDisplayInfo Display, string ColorDir) RequireDisplayAndColorDir(IIccSystemApi api, string? identity)
    {
        var display = SelectDisplay(api, identity);
        if (!display.UsePerUserProfiles)
            throw new InvalidOperationException("当前显示器使用系统级色彩关联，本版本暂不支持切换。");
        var colorDir = api.TryGetColorDirectory() ?? throw new InvalidOperationException("无法读取系统色彩目录。");
        return (display, colorDir);
    }

    /// <summary>备份/漂移保护 → 关联 → 读回验证。profileName 必须已安装在色彩目录内。</summary>
    private static string AssociateWithBackup(IIccSystemApi api, IccDisplayInfo display, string colorDir, string profileName, bool startNewBaselineOnDrift)
    {
        var list = api.ReadAssociationList(display);
        var currentDefault = EffectiveDefault(api, display, colorDir);
        if (currentDefault is null && !(api.CanRestoreAbsentDefault && list.Length == 0))
            throw new InvalidOperationException(
                "未读取到当前生效的显示 profile，为避免无法还原，已拒绝切换。请先在 Windows 颜色管理中确认当前显示器有可用配置文件。");

        var backup = ReadForDisplay(display, forNewApply: startNewBaselineOnDrift);
        if (backup is not null && !IsCompatible(display, list, currentDefault, backup, colorDir))
        {
            if (!startNewBaselineOnDrift) throw new InvalidOperationException(Str.T("Str.IccNewBaselineHint"));
            DisplayBackupFiles.Archive(BackupPath(backup.Identity));
            backup = null;
        }
        if (backup is null)
        {
            backup = new Backup(display.AssociationSubKey, currentDefault, list, DateTime.Now.ToString("s"),
                Identity: DisplayTopology.Identity(display), DisplayName: display.FriendlyName);
        }
        api.ValidateConnection(display);
        backup = backup with { PreviousProfile = currentDefault, ExpectedProfile = profileName };
        WriteBackup(backup);

        api.ValidateConnection(display);
        if (!api.SetDisplayDefaultAssociation(display, profileName))
            throw new InvalidOperationException("系统拒绝了 profile 切换请求（未生效）。");

        // 验证：读回关联列表，最后一个有效 profile 必须是刚设置的预设
        api.ValidateConnection(display);
        var verified = EffectiveDefault(api, display, colorDir);
        if (verified != profileName)
            throw new InvalidOperationException(
                $"切换后验证失败：当前生效 profile 仍是 {verified ?? "<无>"}，系统设置未达成。");

        WriteBackup(backup with { PreviousProfile = null });
        return profileName;
    }

    /// <summary>把用户选中的 profile 复制留档并安装进系统色彩目录（幂等）。返回色彩目录内的文件名。</summary>
    private static string EnsureCustomInstalled(IIccSystemApi api, string sourcePath, string colorDir)
    {
        byte[] bytes = File.ReadAllBytes(sourcePath);
        // 同一个预设永远装成同一个文件名（FpsTune-filter-X.icc → FpsTune-X.icc）；
        // 只有不认识的名字才退回内容哈希，避免系统色彩目录越积越多。
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (stem.StartsWith("FpsTune-filter-", StringComparison.OrdinalIgnoreCase)) stem = stem["FpsTune-filter-".Length..];
        string name = string.IsNullOrWhiteSpace(stem) || stem.Length > 80
            ? $"{CustomProfilePrefix}{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8]}{Path.GetExtension(sourcePath)}"
            : $"FpsTune-{stem}{Path.GetExtension(sourcePath)}";
        string installedPath = Path.Combine(colorDir, name);
        // 与预设同一条幂等口径：已安装且逐字节一致才跳过——用户改了同名源文件后要能重装，
        // 否则屏幕上永远是旧版滤镜（真实投诉来源）。
        if (File.Exists(installedPath) && File.ReadAllBytes(installedPath).SequenceEqual(bytes))
            return name;

        string localPath = Path.Combine(BackupDir(), "custom", name);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        AtomicFile.WriteAllBytes(localPath, bytes);

        InstallIntoColorDir(api, localPath, installedPath);
        return name;
    }

    /// <summary>还原为备份的原始 profile。返回是否执行了还原；无备份时返回 false（如实无事发生）。</summary>
    public static bool Restore(string? identity = null)
    {
        using var gate = SystemMutationGate.Acquire();
        if (!HasRestorableBackup()) return false;
        var api = CreateApi();
        var display = SelectDisplay(api, identity);
        if (!display.UsePerUserProfiles) throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
        var backup = ReadForDisplay(display);
        if (backup is null)
        {
            if (identity is null) throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
            return false;
        }
        if (display.AssociationSubKey != backup.AssociationSubKey)
            throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));

        var beforeRestoreColorDir = api.TryGetColorDirectory() ?? throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
        var currentDefault = EffectiveDefault(api, display, beforeRestoreColorDir);
        if (!IsBackupPlusOwnProfiles(api.ReadAssociationList(display), backup, beforeRestoreColorDir)
            || backup.ExpectedProfile is not null && currentDefault != backup.ExpectedProfile
                && currentDefault != backup.PreviousProfile && currentDefault != backup.DefaultProfile)
            throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));

        api.ValidateConnection(display);
        if (backup.DefaultProfile is not null && !api.SetDisplayDefaultAssociation(display, backup.DefaultProfile))
            throw new InvalidOperationException("系统拒绝了还原请求（未生效），备份已保留，可重试。");

        // 精确还原：写回备份时的完整关联列表（同时清掉本工具追加的预设条目）
        api.WriteAssociationList(display, backup.ListBefore);

        api.ValidateConnection(display);
        var after = api.ReadAssociationList(display);
        var colorDir = api.TryGetColorDirectory();
        var verified = colorDir is null ? null : EffectiveDefault(api, display, colorDir);
        if (!after.SequenceEqual(backup.ListBefore) || verified != backup.DefaultProfile)
            throw new InvalidOperationException("还原后验证失败，备份已保留，可重试。");

        // 清掉我们装进色彩目录、但还原后已不被关联的 profile（含历史 FpsTune-Custom-* 残留）。
        TryRemoveUnusedInstalledProfiles(colorDir, after);
        DeleteBackup(backup.Identity);
        return true;
    }

    /// <summary>只删"我们装的（FpsTune- 前缀）且当前没有任何关联"的文件，系统/厂商 profile 不动。</summary>
    private static void TryRemoveUnusedInstalledProfiles(string? colorDir, string[] associations)
    {
        if (string.IsNullOrWhiteSpace(colorDir) || !Directory.Exists(colorDir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(colorDir, "FpsTune-*.icc"))
            {
                var name = Path.GetFileName(file);
                if (associations.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    /// <summary>把预设 profile 写到本地并安装进系统色彩目录（幂等）。返回系统色彩目录内的文件名。</summary>
    private static string EnsurePresetInstalled(IIccSystemApi api, IccFilterPreset preset, string colorDir)
    {
        var @params = IccProfileGenerator.ParamsFor(preset);
        string installedPath = Path.Combine(colorDir, @params.FileName);
        byte[] bytes = IccProfileGenerator.Build(@params);
        if (File.Exists(installedPath) && File.ReadAllBytes(installedPath).SequenceEqual(bytes))
            return @params.FileName;
        string localPath = Path.Combine(BackupDir(), "presets", @params.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        AtomicFile.WriteAllBytes(localPath, bytes);

        InstallIntoColorDir(api, localPath, installedPath);
        return @params.FileName;
    }

    /// <summary>安装进系统色彩目录并验证可用（预设与用户文件共用；失败抛异常）。</summary>
    private static void InstallIntoColorDir(IIccSystemApi api, string localPath, string installedPath)
    {
        if (!api.InstallColorProfile(localPath))
            throw new InvalidOperationException("安装 profile 到系统色彩目录失败。");
        if (!IccProfileFile.IsDisplayProfile(installedPath))
            throw new InvalidOperationException("安装后未在系统色彩目录找到 profile。");
    }

    /// <summary>生效默认 = per-user 关联列表中从末尾数第一个有效显示类 profile。</summary>
    private static string? EffectiveDefault(IIccSystemApi api, IccDisplayInfo display, string colorDir)
    {
        var name = api.ReadDefaultProfile(display);
        return name is not null && IccProfileFile.IsDisplayProfile(Path.Combine(colorDir, name)) ? name : null;
    }

    /// <summary>当前列表是否 = 备份列表 + 本工具安装的 profile（预设或用户文件；顺序与数量漂移都算外部修改）。</summary>
    private static bool IsBackupPlusOwnProfiles(string[] list, Backup backup, string colorDir)
    {
        var ownNames = AllPresetFileNames();
        // 我们自己装的 profile 一律以 FpsTune- 开头（预设名安装的 FpsTune-<预设名>.icc 也在此列，
        // 不只用历史的自定义哈希前缀）。
        bool Own(string name) => ownNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith("FpsTune-", StringComparison.OrdinalIgnoreCase);
        // Selecting a profile removes its previous entry and appends it as default. Our
        // profiles may therefore move even when they were already in the original list.
        // Foreign entries must still retain exactly their original order and count.
        return list.Where(n => !Own(n)).SequenceEqual(backup.ListBefore.Where(n => !Own(n)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>本工具生成的全部预设文件名（含尚未生成的）。</summary>
    internal static string[] AllPresetFileNames()
        => Enum.GetValues<IccFilterPreset>()
            .Select(p => IccProfileGenerator.FileNameFor(p))
            .Concat(Enum.GetValues<IccFilterPreset>().Select(p => $"FpsTune-{p}.icc"))
            .Concat(new[] { "FpsTune-Neutral-v2.icc", "FpsTune-Muted-v2.icc" }).ToArray();

    private static IccDisplayInfo SelectDisplay(IIccSystemApi api, string? identity)
    {
        var matches = api.GetDisplays().Where(d => identity is null ? d.IsPrimary
            : DisplayTopology.Identity(d).Equals(identity, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(Str.T("Str.DisplayDisconnected"));
        if (!matches[0].Independent) throw new InvalidOperationException(Str.T("Str.DisplayMirrored"));
        api.ValidateConnection(matches[0]);
        return matches[0];
    }
    private static bool IsCompatible(IccDisplayInfo display, string[] list, string? current, Backup backup, string directory)
        => backup.AssociationSubKey == display.AssociationSubKey && IsBackupPlusOwnProfiles(list, backup, directory)
            && (backup.ExpectedProfile is null || new[] { backup.ExpectedProfile, backup.PreviousProfile, backup.DefaultProfile }
                .Contains(current, StringComparer.OrdinalIgnoreCase));
    private static Backup? ReadForDisplay(IccDisplayInfo display, bool forNewApply = false)
    {
        var identity = DisplayTopology.Identity(display);
        var backup = ReadBackup(identity);
        if (backup is not null && (backup.Identity != identity || !forNewApply && backup.AssociationSubKey != display.AssociationSubKey))
            throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
        if (backup is not null) return backup;
        var legacy = ReadBackup();
        if (legacy is null || legacy.AssociationSubKey != display.AssociationSubKey) return null;
        // Old ICC receipts only identified a driver key. Never assign them to a physical monitor without proof.
        if (display.Identity is not null)
        {
            if (forNewApply) return null; // Keep unassigned legacy receipt; a new explicit apply captures its own baseline.
            throw new InvalidOperationException(Str.T("Str.DisplayLegacyBackup"));
        }
        return legacy;
    }
    internal static IReadOnlyList<DisplayBackupTarget> BackupTargets()
    {
        var result = new List<DisplayBackupTarget>();
        foreach (var path in DisplayBackupFiles.List(BackupDir(), "backup"))
        {
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
                var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                if (Path.GetFullPath(path) != Path.GetFullPath(BackupPath(backup.Identity))) throw new InvalidDataException();
                _ = ReadBackup(backup.Identity);
                result.Add(new(backup.Identity, backup.DisplayName ?? Str.T("Str.RecoveryDisplay")));
            }
            catch (Exception ex) { result.Add(new(null, Path.GetFileName(path), OperationDiagnostics.Scrub(ex.Message))); }
        }
        return result;
    }
    public static bool HasRestorableBackup(string? identity = null)
        => identity is null ? DisplayBackupFiles.List(BackupDir(), "backup").Any() : ReadForDisplay(SelectDisplay(CreateApi(), identity)) is not null;
}
