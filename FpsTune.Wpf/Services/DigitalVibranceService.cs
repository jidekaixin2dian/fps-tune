using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FpsTune.Wpf.Core;
using static FpsTune.Wpf.Services.NvapiNative;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 数字振动（Digital Vibrance / DVC）服务：显示级饱和度，**整块屏幕全局生效**
/// （含桌面/网页/游戏），不是按游戏 exe 的 DRS 项。机制走 NVAPI 私有 DVC 接口，
/// ID 与结构对齐 falahati/NvAPIWrapper（GetDVCInfo/SetDVCLevel 系列）。
/// 调整前备份当前档位，可一键还原；作用于选定的独立 NVIDIA 显示器。
/// </summary>
public sealed record VibranceState(bool Supported, int Current, int Min, int Max, int Default, bool Restorable, string? UnsupportedReason = null, string? BackupNotice = null);

public interface INvibranceApi : IDisposable
{
    bool TryInitialize();
    string? LastError { get; }
    /// <summary>读取第一块显示器的 DVC 档位范围与当前值；不支持返回 false。</summary>
    bool TryGetInfo(out int current, out int min, out int max, out int defaultValue);
    /// <summary>写入 DVC 档位（使用 GetInfo 返回的量纲）。失败抛 NvdrsException。</summary>
    void SetLevel(int level);
    string? DisplayIdentity() => null;
    string? LegacyDisplayIdentity() => DisplayIdentity();
    string DisplayName() => Str.T("Str.RecoveryDisplay");
    bool TrySelectDisplay(string? identity) => identity is null || identity == DisplayIdentity();
}

public static class DigitalVibranceService
{
    internal static Func<INvibranceApi>? ApiOverride;
    internal static string? BackupDirOverride;

    private static INvibranceApi CreateApi() => ApiOverride?.Invoke() ?? new NvDvcApi();

    private static string BackupDir()
        => BackupDirOverride ?? Path.Combine(UserDataPaths.Root, "display-quality");

    private static string BackupPath(string? identity = null) => DisplayBackupFiles.PathFor(BackupDir(), "vibrance", identity);

    private sealed record VibranceBackup(int Level, string? Display = null, int? Expected = null, int? Previous = null, int? Min = null, int? Max = null, string? DisplayName = null);

    public static VibranceState GetState(string? identity = null)
    {
        using var api = CreateApi();
        if (!api.TryInitialize())
            return new VibranceState(false, 0, 0, 0, 0, false, api.LastError ?? "NVAPI 不可用");
        if (!api.TrySelectDisplay(identity)) return new VibranceState(false, 0, 0, 0, 0, false, Str.T("Str.DisplayDvcUnavailable"));
        if (!api.TryGetInfo(out var current, out var min, out var max, out var def))
            return new VibranceState(false, 0, 0, 0, 0, false, "当前显示路径不支持数字振动（DVC）。");
        var backup = ReadForDisplay(api);
        var unassigned = backup is null && ReadBackup() is { Display: null };
        var changed = backup is not null && !MatchesBackup(backup, api.DisplayIdentity()!, current, min, max);
        return new VibranceState(true, current, min, max, def, backup is not null,
            BackupNotice: changed ? Str.T("Str.VibranceNewBaselineHint") : unassigned ? Str.T("Str.VibranceLegacyUnassigned") : null);
    }

    public static bool HasRestorableBackup(string? identity = null)
        => identity is null ? DisplayBackupFiles.List(BackupDir(), "vibrance").Any() : ReadBackup(identity) is not null;

    /// <summary>percent 为 0–100（界面量纲），映射到驱动 min–max。</summary>
    public static bool SetPercent(int percent, string? identity = null, bool startNewBaselineOnDrift = false)
    {
        using var gate = SystemMutationGate.Acquire();
        percent = Math.Clamp(percent, 0, 100);
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        if (!api.TrySelectDisplay(identity)) throw new InvalidOperationException(Str.T("Str.DisplayDvcUnavailable"));
        if (!api.TryGetInfo(out var current, out var min, out var max, out var _))
            throw new NvdrsException(-1, "当前显示路径不支持数字振动（DVC）。");

        var display = api.DisplayIdentity() ?? throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
        var backup = ReadForDisplay(api) ?? new VibranceBackup(current, display, Min: min, Max: max, DisplayName: api.DisplayName());
        var rebased = false;
        if (startNewBaselineOnDrift && backup.Display == display && !MatchesBackup(backup, display, current, min, max))
        {
            // A confirmed manual apply is a new intent. Retain the exact old receipt before
            // rebasing; automatic preparation and Restore must still refuse external drift.
            var path = File.Exists(BackupPath(display)) ? BackupPath(display) : BackupPath();
            DisplayBackupFiles.Archive(path);
            backup = new(current, display, Min: min, Max: max, DisplayName: api.DisplayName());
            rebased = true;
        }
        CheckRestorable(backup, display, current, min, max);
        MigrateLegacy(backup, api);
        var level = min + (int)Math.Round((max - min) * (percent / 100.0));
        WriteBackup(backup with { Expected = level, Previous = current });
        api.SetLevel(level);
        VerifyLevel(api, level, min, max);
        WriteBackup(backup with { Expected = level, Previous = null });
        return rebased;
    }

    private static bool MatchesBackup(VibranceBackup backup, string display, int current, int min, int max)
        => backup.Display == display && backup.Min == min && backup.Max == max
            && backup.Level >= min && backup.Level <= max
            && (current == backup.Level || current == backup.Expected || current == backup.Previous);
    private static void CheckRestorable(VibranceBackup backup, string display, int current, int min, int max)
    {
        if (!MatchesBackup(backup, display, current, min, max))
            throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
    }

    /// <summary>还原到进入本功能前的档位。返回是否发生了还原。</summary>
    public static bool Restore(string? identity = null)
    {
        using var gate = SystemMutationGate.Acquire();
        if (!HasRestorableBackup()) return false;
        using var api = CreateApi();
        if (!api.TryInitialize()) throw new NvdrsException(-5, api.LastError ?? "NVAPI unavailable");
        if (!api.TrySelectDisplay(identity)) throw new InvalidOperationException(Str.T("Str.DisplayDvcUnavailable"));
        var backup = ReadForDisplay(api);
        if (backup is null)
        {
            if (identity is null) throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
            return false;
        }
        if (!api.TryGetInfo(out var current, out var min, out var max, out _))
            throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
        CheckRestorable(backup, api.DisplayIdentity() ?? "", current, min, max);
        MigrateLegacy(backup, api);
        api.SetLevel(backup.Level);
        VerifyLevel(api, backup.Level, min, max);
        File.Delete(BackupPath(backup.Display));
        return true;
    }
    private static void VerifyLevel(INvibranceApi api, int expected, int min, int max)
    {
        if (!api.TryGetInfo(out var actual, out var readMin, out var readMax, out _) || actual != expected || min != readMin || max != readMax)
            throw new InvalidOperationException(Str.T("Str.DisplayWriteUnconfirmed"));
    }
    private static VibranceBackup? ReadForDisplay(INvibranceApi api)
    {
        var identity = api.DisplayIdentity();
        if (identity is null) return null;
        var backup = ReadBackup(identity);
        if (backup is not null)
        {
            if (backup.Display != identity) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
            return backup;
        }
        var legacy = ReadBackup();
        if (legacy is null) return null;
        // The oldest release stored only Level. It cannot identify an original for this
        // monitor. Retain the receipt, but start a separate monitor-bound baseline on apply.
        if (legacy.Display is null) return null;
        if (legacy.Display != api.LegacyDisplayIdentity()) return null;
        return legacy with { Display = identity };
    }
    private static void MigrateLegacy(VibranceBackup backup, INvibranceApi api)
    {
        if (!File.Exists(BackupPath(backup.Display)) && File.Exists(BackupPath()))
        {
            if (ReadBackup()?.Display != api.LegacyDisplayIdentity()) return;
            WriteBackup(backup);
            File.Delete(BackupPath()); // A durably persisted, matched receipt now exists under the screen identity.
        }
    }
    internal static IReadOnlyList<DisplayBackupTarget> BackupTargets()
    {
        var result = new List<DisplayBackupTarget>();
        foreach (var path in DisplayBackupFiles.List(BackupDir(), "vibrance"))
        {
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
                var legacy = Path.GetFileName(path) == "vibrance.json";
                var backup = System.Text.Json.JsonSerializer.Deserialize<VibranceBackup>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                if (!legacy && (backup.Display is null || Path.GetFullPath(path) != Path.GetFullPath(BackupPath(backup.Display)))) throw new InvalidDataException();
                _ = ReadBackup(legacy ? null : backup.Display);
                result.Add(new(legacy ? null : backup.Display, backup.DisplayName ?? Str.T("Str.RecoveryDisplay"),
                    backup.Display is null ? Str.T("Str.VibranceLegacyRecovery") : null));
            }
            catch (Exception ex) { result.Add(new(null, Path.GetFileName(path), OperationDiagnostics.Scrub(ex.Message))); }
        }
        return result;
    }

    private static VibranceBackup? ReadBackup(string? identity = null)
    {
        if (!File.Exists(BackupPath(identity)))
            return null;
        try
        {
            if (new FileInfo(BackupPath(identity)).Length > 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
            return System.Text.Json.JsonSerializer.Deserialize<VibranceBackup>(File.ReadAllText(BackupPath(identity)))
                ?? throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"), ex);
        }
    }

    private static void WriteBackup(VibranceBackup backup)
    {
        Directory.CreateDirectory(BackupDir());
        AtomicFile.WriteAllTextDurable(
            BackupPath(backup.Display),
            System.Text.Json.JsonSerializer.Serialize(backup),
            new System.Text.UTF8Encoding(false));
    }


}

/// <summary>真实 DVC 实现（NvAPI_GetDVCInfo / SetDVCLevel）。</summary>
internal sealed class NvDvcApi : INvibranceApi
{
    // 接口 ID（官方 NVIDIA/nvapi nvapi_interface.h）
    // NvAPI_Initialize 的 ID 与 NVAPI 成功状态码在 NvapiNative（DRS 与数字振动共用）
    private const uint IdEnumNvidiaDisplayHandle = 0x9abdd40d;
    private const uint IdGetDvcInfo = 0x4085de45;
    private const uint IdGetDvcInfoEx = 0x0e45002d;
    private const uint IdSetDvcLevel = 0x172409b4;
    private const uint IdSetDvcLevelEx = 0x4a82c2b1;

    private static readonly object Gate = new();
    private static NvDvcApi? _shared;

    private string? _lastError;
    private InitializeDelegate? _initialize;
    private EnumNvidiaDisplayHandleDelegate? _enumDisplay;
    private GetDisplayNameDelegate? _getDisplayName;
    private GetDvcInfoDelegate? _getDvcInfo;
    private GetDvcInfoExDelegate? _getDvcInfoEx;
    private SetDvcLevelDelegate? _setDvcLevel;
    private SetDvcLevelExDelegate? _setDvcLevelEx;
    private uint _lastOutputId;

    public string? LastError => _lastError;
    public void Dispose() { }

    internal NvDvcApi() { }
    private IccDisplayInfo? _target;

    public bool TryInitialize()
    {
        lock (Gate)
        {
            if (_initialize is not null) return true;
            if (_shared?._initialize is not null)
            {
                _initialize = _shared._initialize; _enumDisplay = _shared._enumDisplay; _getDisplayName = _shared._getDisplayName;
                _getDvcInfo = _shared._getDvcInfo; _getDvcInfoEx = _shared._getDvcInfoEx;
                _setDvcLevel = _shared._setDvcLevel; _setDvcLevelEx = _shared._setDvcLevelEx;
                return true;
            }
            try
            {
                if (!TryLoad(out var query, out var loadError))
                {
                    _lastError = loadError;
                    return false;
                }

                // 与 NvidiaDrs 同样：先全部解析到局部变量，**初始化成功后才落字段**，
                // 避免中途失败留下"_initialize 已非空、其余为 null"的半初始化状态
                // （那会让下一次 TryInitialize 误判为已初始化）。
                var initialize = query.Resolve<InitializeDelegate>(IdInitialize);
                var enumDisplay = query.Resolve<EnumNvidiaDisplayHandleDelegate>(IdEnumNvidiaDisplayHandle);
                var getDisplayName = query.Resolve<GetDisplayNameDelegate>(0x22a78b05);

                // Ex 可返回 default/min/max；旧版只有 current/min/max。结构尺寸必须与接口匹配。
                // Ex 与旧接口是互斥的能力探测：取不到 Ex 才回退，探测失败属预期，故吞掉异常。
                GetDvcInfoExDelegate? getDvcInfoEx = null;
                SetDvcLevelExDelegate? setDvcLevelEx = null;
                GetDvcInfoDelegate? getDvcInfo = null;
                SetDvcLevelDelegate? setDvcLevel = null;
                try
                {
                    getDvcInfoEx = query.Resolve<GetDvcInfoExDelegate>(IdGetDvcInfoEx);
                    setDvcLevelEx = query.Resolve<SetDvcLevelExDelegate>(IdSetDvcLevelEx);
                }
                catch
                {
                    getDvcInfo = query.Resolve<GetDvcInfoDelegate>(IdGetDvcInfo);
                    setDvcLevel = query.Resolve<SetDvcLevelDelegate>(IdSetDvcLevel);
                }

                if (initialize() != StatusOk)
                {
                    _lastError = "NVAPI 初始化失败";
                    return false;
                }

                _initialize = initialize;
                _enumDisplay = enumDisplay;
                _getDisplayName = getDisplayName;
                _getDvcInfoEx = getDvcInfoEx;
                _setDvcLevelEx = setDvcLevelEx;
                _getDvcInfo = getDvcInfo;
                _setDvcLevel = setDvcLevel;
                _shared ??= this;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return false;
            }
        }
    }

    public bool TrySelectDisplay(string? identity)
    {
        try { _target = DisplayTopology.Require(identity); _ = SelectedDisplayHandle(); return true; }
        catch (Exception ex) { _target = null; _lastError = ex.Message; return false; }
    }
    private IntPtr SelectedDisplayHandle()
    {
        if (_target is null) throw new InvalidOperationException(Str.T("Str.DisplayDisconnected"));
        DisplayTopology.ValidateConnection(_target);
        var matches = new HashSet<IntPtr>();
        for (uint i = 0; i < 128; i++)
        {
            var handle = IntPtr.Zero;
            if (_enumDisplay!(i, ref handle) != StatusOk) break;
            var name = new StringBuilder(64);
            if (_getDisplayName!(handle, name) == StatusOk && name.ToString().Equals(_target.GdiDeviceName, StringComparison.OrdinalIgnoreCase))
                matches.Add(handle);
        }
        if (matches.Count == 1) return matches.Single();
        throw new InvalidOperationException(Str.T("Str.DisplayDvcUnavailable"));
    }

    public bool TryGetInfo(out int current, out int min, out int max, out int defaultValue)
    {
        current = min = max = defaultValue = 0;
        var display = SelectedDisplayHandle();
        // 不同驱动对 outputId（0 / 0xFFFFFFFF）与 Ex/旧接口的兼容性不一，按组合探测
        foreach (var outputId in new uint[] { 0, 0xffffffff })
        {
            if (_getDvcInfoEx is not null)
            {
                var info = DvcInfoEx.Create();
                if (_getDvcInfoEx(display, outputId, ref info) == StatusOk && info.maxLevel > info.minLevel)
                {
                    current = info.currentLevel;
                    min = info.minLevel;
                    max = info.maxLevel;
                    defaultValue = info.defaultLevel;
                    _lastOutputId = outputId;
                    return true;
                }
            }
            if (_getDvcInfo is not null)
            {
                var legacy = DvcInfo.Create();
                if (_getDvcInfo(display, outputId, ref legacy) == StatusOk && legacy.maxLevel > legacy.minLevel)
                {
                    current = legacy.currentLevel;
                    min = legacy.minLevel;
                    max = legacy.maxLevel;
                    defaultValue = 0;
                    _lastOutputId = outputId;
                    return true;
                }
            }
        }
        return false;
    }

    public string? DisplayIdentity() => _target is null ? null : DisplayTopology.Identity(_target);
    public string? LegacyDisplayIdentity() => _target is null ? null : MscmsIccApi.DisplayIdentity(_target.GdiDeviceName, _target.Identity);
    public string DisplayName() => _target is null ? Str.T("Str.RecoveryDisplay") : $"{_target.FriendlyName} ({_target.GdiDeviceName})";

    public void SetLevel(int level)
    {
        // 必须与读接口同一量纲：GetDVCInfoEx ↔ SetDVCLevelEx（0–100）；旧版 GetDVCInfo ↔ SetDVCLevel
        int status;
        if (_setDvcLevelEx is not null)
        {
            var info = DvcInfoEx.Create();
            info.currentLevel = level;
            status = _setDvcLevelEx(SelectedDisplayHandle(), _lastOutputId, ref info);
        }
        else
        {
            status = _setDvcLevel!(SelectedDisplayHandle(), _lastOutputId, level);
        }
        if (status != StatusOk)
            throw new NvdrsException(status, $"设置数字振动失败（NVAPI {status}）");
    }

    // PrivateDisplayDVCInfo（旧）：version + current + min + max
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct DvcInfo
    {
        public uint version;
        public int currentLevel;
        public int minLevel;
        public int maxLevel;

        internal static DvcInfo Create() => new()
        {
            version = (uint)(Marshal.SizeOf<DvcInfo>() | (1 << 16)),
        };
    }

    // PrivateDisplayDVCInfoEx：多一档 defaultLevel
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct DvcInfoEx
    {
        public uint version;
        public int currentLevel;
        public int minLevel;
        public int maxLevel;
        public int defaultLevel;

        internal static DvcInfoEx Create() => new()
        {
            version = (uint)(Marshal.SizeOf<DvcInfoEx>() | (1 << 16)),
        };
    }

    // 说明：QueryInterface / NvAPI_Initialize 的委托与接口 ID 在 NvapiNative（与 DRS 共用），此处只留 DVC 专属部分。

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int GetDisplayNameDelegate(IntPtr display, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumNvidiaDisplayHandleDelegate(uint index, ref IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDvcInfoDelegate(IntPtr display, uint outputId, ref DvcInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDvcInfoExDelegate(IntPtr display, uint outputId, ref DvcInfoEx info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetDvcLevelDelegate(IntPtr display, uint outputId, int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetDvcLevelExDelegate(IntPtr display, uint outputId, ref DvcInfoEx info);
}
