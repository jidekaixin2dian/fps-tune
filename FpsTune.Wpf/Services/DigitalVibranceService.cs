using System.IO;
using System.Runtime.InteropServices;
using static FpsTune.Wpf.Services.NvapiNative;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 数字振动（Digital Vibrance / DVC）服务：显示级饱和度，**整块屏幕全局生效**
/// （含桌面/网页/游戏），不是按游戏 exe 的 DRS 项。机制走 NVAPI 私有 DVC 接口，
/// ID 与结构对齐 falahati/NvAPIWrapper（GetDVCInfo/SetDVCLevel 系列）。
/// 调整前备份当前档位，可一键还原；作用于第一块 NVIDIA 显示器。
/// </summary>
public sealed record VibranceState(bool Supported, int Current, int Min, int Max, int Default, bool Restorable, string? UnsupportedReason = null);

public interface INvibranceApi : IDisposable
{
    bool TryInitialize();
    string? LastError { get; }
    /// <summary>读取第一块显示器的 DVC 档位范围与当前值；不支持返回 false。</summary>
    bool TryGetInfo(out int current, out int min, out int max, out int defaultValue);
    /// <summary>写入 DVC 档位（使用 GetInfo 返回的量纲）。失败抛 NvdrsException。</summary>
    void SetLevel(int level);
}

public static class DigitalVibranceService
{
    internal static Func<INvibranceApi>? ApiOverride;
    internal static string? BackupDirOverride;

    private static INvibranceApi CreateApi() => ApiOverride?.Invoke() ?? NvDvcApi.Shared;

    private static string BackupDir()
        => BackupDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "display-quality");

    private static string BackupPath => Path.Combine(BackupDir(), "vibrance.json");

    private sealed record VibranceBackup(int Level);

    public static VibranceState GetState()
    {
        using var api = CreateApi();
        if (!api.TryInitialize())
            return new VibranceState(false, 0, 0, 0, 0, false, api.LastError ?? "NVAPI 不可用");
        if (!api.TryGetInfo(out var current, out var min, out var max, out var def))
            return new VibranceState(false, 0, 0, 0, 0, false, "当前显示路径不支持数字振动（DVC）。");
        return new VibranceState(true, current, min, max, def, ReadBackup() is not null);
    }

    public static bool HasRestorableBackup() => ReadBackup() is not null;

    /// <summary>percent 为 0–100（界面量纲），映射到驱动 min–max。</summary>
    public static void SetPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        if (!api.TryGetInfo(out var current, out var min, out var max, out var _))
            throw new NvdrsException(-1, "当前显示路径不支持数字振动（DVC）。");

        if (ReadBackup() is null)
            WriteBackup(new VibranceBackup(current));

        var level = min + (int)Math.Round((max - min) * (percent / 100.0));
        api.SetLevel(level);
    }

    /// <summary>还原到进入本功能前的档位。返回是否发生了还原。</summary>
    public static bool Restore()
    {
        var backup = ReadBackup();
        if (backup is null)
            return false;
        using var api = CreateApi();
        if (!api.TryInitialize())
            throw new NvdrsException(-5, api.LastError ?? "NVAPI 不可用");
        api.SetLevel(backup.Level);
        DeleteBackup();
        return true;
    }

    private static VibranceBackup? ReadBackup()
    {
        if (!File.Exists(BackupPath))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<VibranceBackup>(File.ReadAllText(BackupPath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteBackup(VibranceBackup backup)
    {
        Directory.CreateDirectory(BackupDir());
        AtomicFile.WriteAllText(
            BackupPath,
            System.Text.Json.JsonSerializer.Serialize(backup),
            new System.Text.UTF8Encoding(false));
    }

    private static void DeleteBackup()
    {
        if (File.Exists(BackupPath))
            File.Delete(BackupPath);
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

    internal static NvDvcApi Shared
    {
        get
        {
            lock (Gate)
            {
                _shared ??= new NvDvcApi();
                return _shared;
            }
        }
    }

    private string? _lastError;
    private InitializeDelegate? _initialize;
    private EnumNvidiaDisplayHandleDelegate? _enumDisplay;
    private GetDvcInfoDelegate? _getDvcInfo;
    private GetDvcInfoExDelegate? _getDvcInfoEx;
    private SetDvcLevelDelegate? _setDvcLevel;
    private SetDvcLevelExDelegate? _setDvcLevelEx;
    private uint _lastOutputId;

    public string? LastError => _lastError;
    public void Dispose() { }

    private NvDvcApi() { }

    public bool TryInitialize()
    {
        lock (Gate)
        {
            if (_initialize is not null)
                return true;
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
                _getDvcInfoEx = getDvcInfoEx;
                _setDvcLevelEx = setDvcLevelEx;
                _getDvcInfo = getDvcInfo;
                _setDvcLevel = setDvcLevel;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return false;
            }
        }
    }

    private IntPtr PrimaryDisplayHandle()
    {
        var handle = IntPtr.Zero;
        var status = _enumDisplay!(0, ref handle);
        if (status != StatusOk || handle == IntPtr.Zero)
            throw new NvdrsException(status, "未找到 NVIDIA 显示器句柄");
        return handle;
    }

    public bool TryGetInfo(out int current, out int min, out int max, out int defaultValue)
    {
        current = min = max = defaultValue = 0;
        var display = PrimaryDisplayHandle();
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

    public void SetLevel(int level)
    {
        // 必须与读接口同一量纲：GetDVCInfoEx ↔ SetDVCLevelEx（0–100）；旧版 GetDVCInfo ↔ SetDVCLevel
        int status;
        if (_setDvcLevelEx is not null)
        {
            var info = DvcInfoEx.Create();
            info.currentLevel = level;
            status = _setDvcLevelEx(PrimaryDisplayHandle(), _lastOutputId, ref info);
        }
        else
        {
            status = _setDvcLevel!(PrimaryDisplayHandle(), _lastOutputId, level);
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
