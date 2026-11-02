using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 全屏方案「我改过系统」的落盘凭证。存在的意义只有一个：程序崩溃/被杀后下次启动能还原。
/// gamma 通道必须连原始 ramp 一起存（还原就是把它写回去），ICC 通道只需身份与预设名。
/// </summary>
internal sealed record FullscreenPending(
    int Version,
    string Identity,
    string Channel,
    string Preset,
    DateTime AppliedAt,
    ushort[]? Original = null,
    ushort[]? Expected = null);

/// <summary>单文件凭证（全机只有一份：同一时刻只允许一个显示器处于全屏方案状态）。</summary>
internal static class FullscreenPendingStore
{
    internal const int CurrentVersion = 1;
    private const int RampLength = 768;

    /// <summary>测试注入：换目录等于换文件。</summary>
    internal static string? DirectoryOverride;

    internal static string DirectoryPath => DirectoryOverride ?? Path.Combine(UserDataPaths.Root, "fullscreen-preset");

    internal static string FilePath => Path.Combine(DirectoryPath, "pending.json");

    internal static FullscreenPending? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > 256 * 1024) throw new InvalidDataException(Str.T("Str.ColorBackupLarge"));
            return Validate(JsonSerializer.Deserialize<FullscreenPending>(File.ReadAllText(FilePath, Encoding.UTF8)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            // 损坏的凭证不能静默当成「没有改动」，但也不能让启动崩掉：留档后清掉。
            try { AtomicFile.PreserveCorrupt(FilePath); } catch { }
            try { File.Delete(FilePath); } catch { }
            return null;
        }
    }

    internal static void Save(FullscreenPending pending)
    {
        _ = Validate(pending);
        System.IO.Directory.CreateDirectory(DirectoryPath);
        AtomicFile.WriteAllTextDurable(FilePath, JsonSerializer.Serialize(pending), new UTF8Encoding(false));
    }

    internal static void Delete()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 删不掉不影响还原结果；下次启动会再试一次还原（幂等）。
        }
    }

    internal static FullscreenPending? Validate(FullscreenPending? pending)
    {
        if (pending is null) return null;
        if (pending.Version != CurrentVersion) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        if (string.IsNullOrWhiteSpace(pending.Identity) || pending.Identity.Length > 4096)
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        if (pending.Channel is not (FullscreenPresetWatcher.ChannelGamma or FullscreenPresetWatcher.ChannelIcc))
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        if (pending.Channel == FullscreenPresetWatcher.ChannelGamma
            && (pending.Original?.Length != RampLength || pending.Expected?.Length != RampLength))
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        return pending;
    }
}

/// <summary>
/// gamma 通道：对**当前 ramp 快照**做增量，而不是从零生成整条曲线。
///
/// 差别很关键——用户在「画面与色彩」里调过的亮度/对比/色温都写在当前 LUT 里，
/// 从零生成会把那些设定直接抹掉。这里复用 ScreenColorService 的同一条映射
/// （BuildRamp 以进入时的快照为基线重映射），退出时把快照原样写回。
/// </summary>
internal static class TransientGamma
{
    /// <summary>测试注入；产品运行时走真实 ScreenGammaApi。</summary>
    internal static IScreenGammaApi? ApiOverride;

    private static IScreenGammaApi Api => ApiOverride ?? new ScreenGammaApi();

    /// <summary>应用预设并返回落盘凭证。失败抛异常，系统状态只可能停在已验证的位置。</summary>
    internal static FullscreenPending Apply(string identity, IccFilterPreset preset)
    {
        using var gate = SystemMutationGate.Acquire();
        var display = DisplayTopology.Require(identity);
        DisplayTopology.ValidateConnection(display);
        if (Api.IsHdr(display)) throw new InvalidOperationException(Str.T("Str.ColorHdr"));

        var snapshot = Api.Read(display);
        var expected = ScreenColorService.BuildRamp(ScreenColorSettings.ForPreset(preset), snapshot);
        var pending = new FullscreenPending(FullscreenPendingStore.CurrentVersion, identity,
            FullscreenPresetWatcher.ChannelGamma, preset.ToString(), DateTime.Now, snapshot, expected);

        // 先落盘再改屏：这两步之间崩溃也不能丢掉还原依据。
        FullscreenPendingStore.Save(pending);
        try
        {
            Api.Write(display, expected);
            if (!ScreenColorService.Matches(Api.Read(display), expected))
            {
                // 读回不一致说明这版 LUT 没被接受：立即把快照写回去，别把失败留在屏上。
                try { Api.Write(display, snapshot); } catch { }
                throw new InvalidOperationException(Str.T("Str.ColorRefused"));
            }
        }
        catch
        {
            // 屏上要么仍是快照、要么已回滚，凭证没有意义，删掉避免下次启动误还原。
            FullscreenPendingStore.Delete();
            throw;
        }

        return pending;
    }

    /// <summary>把凭证里的原始 ramp 写回。外部已改动或 HDR 变化时拒绝覆盖。</summary>
    internal static bool Restore(FullscreenPending pending)
    {
        if (pending.Channel != FullscreenPresetWatcher.ChannelGamma) return false;
        if (pending.Original is not { Length: 768 } original || pending.Expected is not { Length: 768 } expected)
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));

        using var gate = SystemMutationGate.Acquire();
        var display = DisplayTopology.Require(pending.Identity);
        DisplayTopology.ValidateConnection(display);
        if (Api.IsHdr(display)) throw new InvalidOperationException(Str.T("Str.ColorHdr"));

        // 已经回到原始曲线（用户手动还原过，或上一次退出时已写回）：凭证的用途已经满足，
        // 这里算还原完成，不再按“被外部接管”报失败——否则每次启动都会重复报错并永久留着凭证。
        var current = Api.Read(display);
        if (ScreenColorService.Matches(current, original)) return true;

        // 只还原「确实是本工具写进去的那一版」：用户自己又调过色就别盖回去。
        if (!ScreenColorService.Matches(current, expected))
            throw new InvalidOperationException(Str.T("Str.ColorRestoreDrift"));

        Api.Write(display, original);
        if (!ScreenColorService.Matches(Api.Read(display), original))
            throw new InvalidOperationException(Str.T("Str.ColorRestoreFailed"));
        return true;
    }
}
