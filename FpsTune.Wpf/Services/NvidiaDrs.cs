using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

/// <summary>
/// NVAPI DRS（驱动设置仓库）互操作层。
/// 全部接口 ID 与 SettingID 来自 NVIDIA 官方仓库 NVIDIA/nvapi（nvapi_interface.h /
/// NvApiDriverSettings.h，2026-09 拉取核对）；结构布局与调用约定对照
/// nvidiaProfileInspector / DLSSTweaks 两个久经验证的 C# 实现交叉确认：
/// DRS_SetSetting / DRS_GetSetting / DRS_DeleteProfileSetting / DRS_FindApplicationByName
/// 经 QueryInterface 解析后带隐藏尾参（调用时传 0）。
/// 只管理本工具自建（"FpsTune · " 前缀）的 profile，还原绝不触碰用户或其他工具的配置。
/// 注意：刻意不使用 FindProfileByName 与 GetProfileInfo——两者在本机新驱动上
/// 经互操作调用会访问冲突或产出不可用句柄（2026-09-13 真机实证）。
/// </summary>
internal interface INvdrsApi : IDisposable
{
    /// <summary>加载 nvapi64.dll 并解析所需接口；非 N 卡驱动环境返回 false（LastError 说明原因）。</summary>
    bool TryInitialize();

    string? LastError { get; }

    /// <summary>打开 DRS 会话并加载当前驱动设置；失败抛 NvdrsException。Dispose 销毁会话。</summary>
    INvdrsSession OpenSession();
}

/// <summary>一次 DRS 会话：Dispose 时销毁会话（未显式 Save 的修改一并丢弃）。</summary>
internal interface INvdrsSession : IDisposable
{
    /// <summary>按可执行文件名查找登记了该游戏的 profile（多为 NVIDIA 预置的游戏 profile）；没有返回 null。</summary>
    INvdrsProfile? FindApplicationOwner(string exeName);

    /// <summary>创建 profile 并把游戏 exe 挂为其应用；失败抛 NvdrsException。</summary>
    INvdrsProfile CreateProfile(string name, string gameExe);

    /// <summary>读取 DWORD 设置；未设置返回 false（正常状态，不是错误）。</summary>
    bool TryGetSettingDword(INvdrsProfile profile, uint settingId, out uint value);

    /// <summary>写入 DWORD 设置；失败抛 NvdrsException。</summary>
    void SetSettingDword(INvdrsProfile profile, uint settingId, uint value);

    /// <summary>删除 profile 上的一条设置；未设置返回 false。</summary>
    bool DeleteSetting(INvdrsProfile profile, uint settingId);

    /// <summary>删除 profile；失败抛 NvdrsException。</summary>
    void DeleteProfile(INvdrsProfile profile);

    /// <summary>持久化到驱动；失败抛 NvdrsException。</summary>
    void Save();
}

internal interface INvdrsProfile
{
    /// <summary>原生 profile 句柄（用于同会话内身份比较）。</summary>
    IntPtr Handle { get; }
}

/// <summary>NVAPI 操作失败，Message 为含状态码的中文说明。</summary>
internal sealed class NvdrsException : Exception
{
    public int Status { get; }

    public NvdrsException(int status, string message) : base(message) => Status = status;
}

/// <summary>真实 NVAPI 实现。进程内单例（Shared），所有操作经 Gate 串行。</summary>
internal sealed class NvdrsApi : INvdrsApi
{
    // 接口 ID（官方 NVIDIA/nvapi 仓库 nvapi_interface.h；Set/Get/Delete 有新旧两套，优先新）
    private const uint IdInitialize = 0x0150e828;
    private const uint IdGetErrorMessage = 0x6c2d048c;
    private const uint IdDrsCreateSession = 0x0694d52e;
    private const uint IdDrsDestroySession = 0xdad9cff8;
    private const uint IdDrsLoadSettings = 0x375dbd6b;
    private const uint IdDrsSaveSettings = 0xfcbc7e14;
    private const uint IdDrsCreateProfile = 0xcc176068;
    private const uint IdDrsDeleteProfile = 0x17093206;
    private const uint IdDrsFindApplicationByName = 0xeee566b2;
    private const uint IdDrsCreateApplication = 0x4347a9de;
    private const uint IdDrsSetSetting = 0x8a2cf5f5;           // 旧回退 0x577dd202
    private const uint IdDrsGetSetting = 0xea99498d;           // 旧回退 0x73bf8338
    private const uint IdDrsDeleteProfileSetting = 0xd20d29df; // 旧回退 0xe4a26362

    // NVAPI 状态码（官方头 NvAPI_Status 枚举；nvidiaProfileInspector 实现核对）
    private const int StatusOk = 0;
    internal const int StatusSettingNotFound = -160;
    private const int StatusProfileNotFound = -163;
    private const int StatusExecutableNotFound = -166;

    internal const uint NvapiUnicodeStringMax = 2048;
    private const int UnionDataSize = 4096; // NVAPI_BINARY_DATA_MAX

    private static readonly object Gate = new();
    private static NvdrsApi? _shared;

    internal static NvdrsApi Shared
    {
        get
        {
            lock (Gate)
            {
                _shared ??= new NvdrsApi();
                return _shared;
            }
        }
    }

    private IntPtr _nvapiDll;
    private string? _lastError;
    private NvapiInitializeDelegate? _initialize;
    private DrsCreateSessionDelegate? _createSession;
    private DrsDestroySessionDelegate? _destroySession;
    private DrsLoadSettingsDelegate? _loadSettings;
    private DrsSaveSettingsDelegate? _saveSettings;
    private DrsCreateProfileDelegate? _createProfile;
    private DrsDeleteProfileDelegate? _deleteProfile;
    private DrsFindApplicationByNameDelegate? _findApplicationByName;
    private DrsCreateApplicationDelegate? _createApplication;
    private DrsSetSettingDelegate? _setSetting;
    private DrsGetSettingDelegate? _getSetting;
    private DrsDeleteProfileSettingDelegate? _deleteProfileSetting;
    private DrsGetErrorMessageDelegate? _getErrorMessage;

    private NvdrsApi() { }

    public string? LastError => _lastError;

    // NVAPI 库进程级常驻，不需要卸载（显式 Unload 反而与驱动全局状态竞态）
    public void Dispose() { }

    public bool TryInitialize()
    {
        lock (Gate)
        {
            if (_initialize is not null)
                return true;
            try
            {
                _nvapiDll = LoadLibrary(IntPtr.Size == 4 ? "nvapi.dll" : "nvapi64.dll");
                if (_nvapiDll == IntPtr.Zero)
                {
                    _lastError = "未找到 NVIDIA 驱动库（nvapi64.dll），本机可能不是 NVIDIA 显卡。";
                    return false;
                }
                var query = (NvapiQueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
                    GetProcAddress(_nvapiDll, "nvapi_QueryInterface"), typeof(NvapiQueryInterfaceDelegate));
                T Resolve<T>(uint primaryId, uint? fallbackId) where T : class
                {
                    var ptr = query(primaryId);
                    if (ptr == IntPtr.Zero && fallbackId is { } fallback)
                        ptr = query(fallback);
                    if (ptr == IntPtr.Zero)
                        throw new InvalidOperationException($"NVAPI 接口 {primaryId:X8} 不可用");
                    return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T
                        ?? throw new InvalidOperationException($"NVAPI 接口 {primaryId:X8} 签名解析失败");
                }

                _initialize = Resolve<NvapiInitializeDelegate>(IdInitialize, null);
                _createSession = Resolve<DrsCreateSessionDelegate>(IdDrsCreateSession, null);
                _destroySession = Resolve<DrsDestroySessionDelegate>(IdDrsDestroySession, null);
                _loadSettings = Resolve<DrsLoadSettingsDelegate>(IdDrsLoadSettings, null);
                _saveSettings = Resolve<DrsSaveSettingsDelegate>(IdDrsSaveSettings, null);
                _createProfile = Resolve<DrsCreateProfileDelegate>(IdDrsCreateProfile, null);
                _deleteProfile = Resolve<DrsDeleteProfileDelegate>(IdDrsDeleteProfile, null);
                _findApplicationByName = Resolve<DrsFindApplicationByNameDelegate>(IdDrsFindApplicationByName, null);
                _createApplication = Resolve<DrsCreateApplicationDelegate>(IdDrsCreateApplication, null);
                _setSetting = Resolve<DrsSetSettingDelegate>(IdDrsSetSetting, 0x577dd202);
                _getSetting = Resolve<DrsGetSettingDelegate>(IdDrsGetSetting, 0x73bf8338);
                _deleteProfileSetting = Resolve<DrsDeleteProfileSettingDelegate>(IdDrsDeleteProfileSetting, 0xe4a26362);
                _getErrorMessage = Resolve<DrsGetErrorMessageDelegate>(IdGetErrorMessage, null);

                var status = _initialize();
                if (status != StatusOk)
                {
                    _lastError = ErrorMessage(status) is { Length: > 0 } detail
                        ? $"NVAPI 初始化失败：{detail}（状态码 {status}）"
                        : $"NVAPI 初始化失败（状态码 {status}）";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return false;
            }
        }
    }

    public INvdrsSession OpenSession()
    {
        lock (Gate)
        {
            if (_initialize is null)
                throw new NvdrsException(-5, LastError ?? "NVAPI 未初始化");

            var session = IntPtr.Zero;
            try
            {
                Check(_createSession(ref session), "打开驱动设置会话");
                Check(_loadSettings(session), "加载驱动设置");
                return new SessionImpl(this, session);
            }
            catch
            {
                if (session != IntPtr.Zero)
                    _destroySession?.Invoke(session);
                throw;
            }
        }
    }

    private void Check(int status, string what)
    {
        if (status == StatusOk)
            return;
        var detail = _getErrorMessage is null ? null : ErrorMessage(status);
        throw new NvdrsException(status,
            detail is { Length: > 0 }
                ? $"{what}失败：{detail}（NVAPI {status}）"
                : $"{what}失败（NVAPI 状态码 {status}）");
    }

    private string? ErrorMessage(int status)
    {
        // NvAPI_ShortString 是 ANSI char[64]，不是 UTF-16——按宽字符读会得到乱码
        var buffer = new byte[64];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            _getErrorMessage!(status, pinned.AddrOfPinnedObject());
            var text = Marshal.PtrToStringAnsi(pinned.AddrOfPinnedObject());
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>分配清零的设置结构缓冲（含 1KB 尾部余量），写入结构后返回。</summary>
    private static IntPtr AllocSettingBuffer(NvdrsSetting setting, out object keepAlive)
    {
        var size = Marshal.SizeOf<NvdrsSetting>();
        var buffer = Marshal.AllocHGlobal(size + 65536);
        Marshal.Copy(new byte[size + 65536], 0, buffer, size + 65536);
        Marshal.StructureToPtr(setting, buffer, false);
        keepAlive = setting; // ByValTStr 字符串封送期间防回收
        return buffer;
    }

    internal static NvdrsSetting NewSetting(uint settingId)
    {
        // settingName 保持 null（与实测可用的最小调用一致）；类型 DWORD
        return new NvdrsSetting
        {
            version = MakeVersion<NvdrsSetting>(1),
            settingId = settingId,
            settingType = 0,
            predefinedValue = SettingUnion.Create(),
            currentValue = SettingUnion.Create(),
        };
    }

    internal static ushort[] ToUnicodeString(string value)
    {
        var buffer = new ushort[NvapiUnicodeStringMax];
        var source = value ?? "";
        for (var i = 0; i < source.Length && i < NvapiUnicodeStringMax - 1; i++)
            buffer[i] = source[i];
        return buffer;
    }

    private static uint MakeVersion<T>(int version) where T : struct
        => (uint)(Marshal.SizeOf<T>() | (version << 16));

    private sealed class SessionImpl(NvdrsApi api, IntPtr session) : INvdrsSession
    {
        public void Dispose() => api._destroySession?.Invoke(session);

        public INvdrsProfile? FindApplicationOwner(string exeName)
        {
            // 手工缓冲区封送（零初始化 + 8KB 尾部余量）：驱动回写可能大于托管结构尺寸
            var profileHandle = Marshal.AllocHGlobal(IntPtr.Size);
            var application = new NvdrsApplication
            {
                version = MakeVersion<NvdrsApplication>(3),
                appName = exeName,
            };
            var appSize = Marshal.SizeOf<NvdrsApplication>();
            var appBuffer = Marshal.AllocHGlobal(appSize + 8192);
            try
            {
                Marshal.Copy(new byte[appSize + 8192], 0, appBuffer, appSize + 8192);
                Marshal.StructureToPtr(application, appBuffer, false);
                var status = api._findApplicationByName(session, NvdrsApi.ToUnicodeString(exeName), ref profileHandle, appBuffer);
                if (status == StatusExecutableNotFound || status == StatusProfileNotFound)
                    return null;
                api.Check(status, $"查找登记了 {exeName} 的配置文件");
                return new ProfileImpl(api, session, Marshal.ReadIntPtr(profileHandle));
            }
            finally
            {
                Marshal.DestroyStructure<NvdrsApplication>(appBuffer);
                Marshal.FreeHGlobal(appBuffer);
                Marshal.FreeHGlobal(profileHandle);
            }
        }

        public INvdrsProfile CreateProfile(string name, string gameExe)
        {
            var profile = new NvdrsProfile
            {
                version = MakeVersion<NvdrsProfile>(1),
                profileName = name,
            };
            var handle = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                api.Check(api._createProfile(session, ref profile, ref handle), $"创建配置文件“{name}”");
                var profilePtr = Marshal.ReadIntPtr(handle);
                var application = new NvdrsApplication
                {
                    version = MakeVersion<NvdrsApplication>(3),
                    appName = gameExe,
                    userFriendlyName = name,
                    launcher = "",
                    fileInFolder = "",
                    bitvector1 = 0,
                };
                api.Check(api._createApplication(session, profilePtr, ref application), $"登记游戏主程序 {gameExe}");
                return new ProfileImpl(api, session, profilePtr);
            }
            finally
            {
                Marshal.FreeHGlobal(handle);
            }
        }

        public bool TryGetSettingDword(INvdrsProfile profile, uint settingId, out uint value)
        {
            // 手工缓冲区封送：零初始化 + 尾部余量
            var setting = NvdrsApi.NewSetting(settingId);
            uint extra = 0;
            var buffer = NvdrsApi.AllocSettingBuffer(setting, out var keepAlive);
            try
            {
                var status = api._getSetting(session, ProfilePtr(profile), settingId, buffer, ref extra);
                if (status == StatusSettingNotFound)
                {
                    value = 0;
                    return false;
                }
                api.Check(status, $"读取设置 {settingId:X8}");
                value = Marshal.PtrToStructure<NvdrsSetting>(buffer).currentValue.u32Value;
                return true;
            }
            finally
            {
                Marshal.DestroyStructure<NvdrsSetting>(buffer);
                Marshal.FreeHGlobal(buffer);
                GC.KeepAlive(keepAlive);
            }
        }

        public void SetSettingDword(INvdrsProfile profile, uint settingId, uint value)
        {
            var setting = NvdrsApi.NewSetting(settingId);
            setting.currentValue.u32Value = value;
            var buffer = NvdrsApi.AllocSettingBuffer(setting, out var keepAlive);
            try
            {
                api.Check(api._setSetting(session, ProfilePtr(profile), buffer, 0, 0),
                    $"写入设置 {settingId:X8}");
            }
            finally
            {
                Marshal.DestroyStructure<NvdrsSetting>(buffer);
                Marshal.FreeHGlobal(buffer);
                GC.KeepAlive(keepAlive);
            }
        }

        public bool DeleteSetting(INvdrsProfile profile, uint settingId)
        {
            var status = api._deleteProfileSetting(session, ProfilePtr(profile), settingId, 0);
            if (status == StatusSettingNotFound)
                return false;
            api.Check(status, $"删除设置 {settingId:X8}");
            return true;
        }

        public void DeleteProfile(INvdrsProfile profile)
            => api.Check(api._deleteProfile(session, ProfilePtr(profile)), "删除配置文件");

        public void Save() => api.Check(api._saveSettings(session), "保存驱动设置");
    }

    private sealed class ProfileImpl(NvdrsApi api, IntPtr session, IntPtr ptr) : INvdrsProfile
    {
        public IntPtr Handle => ptr;
    }

    private static IntPtr ProfilePtr(INvdrsProfile profile)
        => profile is ProfileImpl impl
            ? impl.Handle
            : throw new ArgumentException("profile 不属于本会话", nameof(profile));

    // ---------- 结构布局（对照 nvidiaProfileInspector / DLSSTweaks，Pack 8 + Unicode ByValTStr） ----------

    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    internal struct NvdrsSetting
    {
        public uint version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)NvapiUnicodeStringMax)]
        public string settingName;
        public uint settingId;
        public uint settingType;
        public uint settingLocation;
        public uint isCurrentPredefined;
        public uint isPredefinedValid;
        public SettingUnion predefinedValue;
        public SettingUnion currentValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct SettingUnion
    {
        public uint u32Value;
        // 与 C 端 union 的 binary 成员等宽（valueLength 4 + data 4096），保证布局一致
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = UnionDataSize)]
        public byte[] padding;

        internal static SettingUnion Create() => new() { padding = new byte[UnionDataSize] };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    internal struct NvdrsProfile
    {
        public uint version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)NvapiUnicodeStringMax)]
        public string profileName;
        public uint gpuSupport;
        public uint isPredefined;
        public uint numOfApps;
        public uint numOfSettings;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    internal struct NvdrsApplication
    {
        // V3 布局（nvidiaProfileInspector/DLSSTweaks 现行实现一致；V1 会被新驱动以 INVALID_ARGUMENT 拒绝）
        public uint version;
        public uint isPredefined;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)NvapiUnicodeStringMax)]
        public string appName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)NvapiUnicodeStringMax)]
        public string userFriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)NvapiUnicodeStringMax)]
        public string launcher;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)NvapiUnicodeStringMax)]
        public string fileInFolder;
        public uint bitvector1; // isMetro/isCommandLine 标志位，恒 0
    }

    #region NVAPI 委托（签名经 nvidiaProfileInspector / DLSSTweaks 交叉核对；Cdecl）

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvapiQueryInterfaceDelegate(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvapiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsCreateSessionDelegate(ref IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDestroySessionDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsLoadSettingsDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsSaveSettingsDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsCreateProfileDelegate(IntPtr session, ref NvdrsProfile profile, ref IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDeleteProfileDelegate(IntPtr session, IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsFindApplicationByNameDelegate(IntPtr session, ushort[] appName, ref IntPtr handle, IntPtr application);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsCreateApplicationDelegate(IntPtr session, IntPtr profile, ref NvdrsApplication application);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsSetSettingDelegate(IntPtr session, IntPtr profile, IntPtr setting, uint extra1, uint extra2);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsGetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, IntPtr setting, ref uint extra);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDeleteProfileSettingDelegate(IntPtr session, IntPtr profile, uint settingId, uint extra);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsGetErrorMessageDelegate(int status, IntPtr message);

    #endregion

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
