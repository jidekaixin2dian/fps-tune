using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FpsTune.Wpf.Services;

/// <summary>主显示器信息：GDI 设备名、显示 LUID/source、注册表关联子键名、是否按用户关联。</summary>
public sealed record IccDisplayInfo(
    string GdiDeviceName,
    uint AdapterIdLow,
    int AdapterIdHigh,
    uint SourceId,
    string AssociationSubKey,
    bool UsePerUserProfiles);

/// <summary>
/// mscms/系统色彩层抽象：单测注入内存态假实现，正式路径见 MscmsIccApi。
/// 机制均经真机探针验证（2026-09-14，Windows 11 26200）：
///  - 切换默认 = ColorProfileSetDisplayDefaultAssociation(CURRENT_USER, CPT_ICM, CPST_NONE)，
///    行为是把 profile 追加到 per-user 显示关联列表末尾（默认 = 列表中最后一个有效 profile）；
///  - 旧 AssociateColorProfileWithDeviceW 在该系统上把 "DISPLAY1" 当捕获设备处理（写错存储），
///    WcsSetDefaultColorProfile 返回 TRUE 但静默无操作——均不可用；
///  - GetICMProfile 不反映真实默认（始终报 sRGB），读端以关联列表（注册表）为准；
///  - ColorProfileGetDisplayList 在该系统上访问冲突，不可用。
/// </summary>
internal interface IIccSystemApi
{
    /// <summary>定位主显示器；失败返回 false（多显示器环境只作用主显示器）。</summary>
    bool TryGetPrimaryDisplay(out IccDisplayInfo display);

    /// <summary>系统色彩目录（COLOR directory）；失败返回 null。</summary>
    string? TryGetColorDirectory();

    bool InstallColorProfile(string sourcePath);

    /// <summary>把 profile 追加为当前用户显示关联列表的默认（列表末尾）。</summary>
    bool SetDisplayDefaultAssociation(IccDisplayInfo display, string profileName);

    string[] ReadAssociationList(IccDisplayInfo display);

    /// <summary>精确写回关联列表（还原语义）。仅允许写主显示器的 per-user 列表。</summary>
    void WriteAssociationList(IccDisplayInfo display, string[] list);

    /// <summary>用系统 mscms 校验 profile 字节（只读冒烟验证，不写任何系统状态）。</summary>
    bool IsProfileBytesValid(byte[] profile);
}

/// <summary>真实 mscms/注册表实现。</summary>
public sealed class MscmsIccApi : IIccSystemApi
{
    private const string AssociationClassGuid = "{4d36e96e-e325-11ce-bfc1-08002be10318}";
    private const string AssociationBasePath =
        @"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\" + AssociationClassGuid;
    private const uint QdcOnlyActivePaths = 2;
    private const uint WcsScopeCurrentUser = 1;
    private const uint ProfileTypeIcm = 0;
    private const uint ProfileSubTypeNone = 4; // CPT_ICM + CPST_NONE = 设备默认 ICC

    public bool TryGetPrimaryDisplay(out IccDisplayInfo display)
    {
        display = new IccDisplayInfo("", 0, 0, 0, "", false);
        string? gdiName = GetPrimaryGdiDeviceName();
        if (string.IsNullOrEmpty(gdiName))
            return false;

        if (!TryGetDisplayId(gdiName, out uint lo, out int hi, out uint src))
            return false;

        // 监视器驱动键名（如 "0004"）即 per-user 关联列表的注册表子键
        string? subKey = GetPrimaryMonitorSubKey(gdiName);
        if (string.IsNullOrEmpty(subKey))
            return false;

        bool perUser = true;
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AssociationBasePath + "\\" + subKey))
        {
            if (key is not null)
            {
                object? v = key.GetValue("UsePerUserProfiles");
                if (v is int i)
                    perUser = i != 0;
            }
        }

        display = new IccDisplayInfo(gdiName, lo, hi, src, subKey, perUser);
        return true;
    }

    public string? TryGetColorDirectory()
    {
        var dir = new StringBuilder(300);
        uint size = 300;
        return GetColorDirectoryW(null, dir, ref size) ? dir.ToString() : null;
    }

    public bool InstallColorProfile(string sourcePath)
        => InstallColorProfileW(null, sourcePath);

    public bool SetDisplayDefaultAssociation(IccDisplayInfo display, string profileName)
        => ColorProfileSetDisplayDefaultAssociation(
            WcsScopeCurrentUser, profileName, ProfileTypeIcm, ProfileSubTypeNone,
            display.AdapterIdLow, display.AdapterIdHigh, display.SourceId) == 0;

    public string[] ReadAssociationList(IccDisplayInfo display)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AssociationBasePath + "\\" + display.AssociationSubKey);
        return (key?.GetValue("ICMProfile") as string[]) ?? Array.Empty<string>();
    }

    public void WriteAssociationList(IccDisplayInfo display, string[] list)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AssociationBasePath + "\\" + display.AssociationSubKey);
        key.SetValue("ICMProfile", list, Microsoft.Win32.RegistryValueKind.MultiString);
    }

    public bool IsProfileBytesValid(byte[] profile)
    {
        var pinned = GCHandle.Alloc(profile, GCHandleType.Pinned);
        try
        {
            var p = new PROFILE
            {
                dwType = ProfileMemBuffer,
                pProfileData = pinned.AddrOfPinnedObject(),
                cbData = (uint)profile.Length,
            };
            IntPtr h = OpenColorProfileW(ref p, ProfileRead, 0, 0);
            if (h == IntPtr.Zero)
                return false;
            try
            {
                return IsColorProfileValid(h, out bool valid) && valid;
            }
            finally
            {
                CloseColorProfile(h);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    // ---- 主显示器定位 ----

    private static string? GetPrimaryGdiDeviceName()
    {
        string? name = null;
        Monitorenumproc cb = (hMonitor, hdc, ref rect, data) =>
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(info);
            if (GetMonitorInfo(hMonitor, ref info) && (info.dwFlags & MonitorinfofPrimary) != 0)
                name = info.szDevice;
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        return name;
    }

    /// <summary>在活动显示路径里找 GDI 设备名对应的 LUID + sourceID。</summary>
    private static bool TryGetDisplayId(string gdiDeviceName, out uint adapterLow, out int adapterHigh, out uint sourceId)
    {
        adapterLow = 0;
        adapterHigh = 0;
        sourceId = 0;
        uint nPaths = 0, nModes = 0;
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, ref nPaths, ref nModes) != 0 || nPaths == 0)
            return false;

        // 元素间距按 512 字节冗余分配：真正读取的只有每个元素开头 12 字节
        // （sourceInfo = LUID(8) + id(4)），首元素固定从缓冲区起始。
        IntPtr paths = Marshal.AllocHGlobal((int)(nPaths * 512));
        IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nModes) * 512);
        try
        {
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref nPaths, paths, ref nModes, modes, IntPtr.Zero) != 0)
                return false;

            for (int i = 0; i < (int)nPaths; i++)
            {
                IntPtr p = paths + i * 512;
                uint lo = (uint)Marshal.ReadInt32(p, 0);
                int hi = Marshal.ReadInt32(p, 4);
                uint src = (uint)Marshal.ReadInt32(p, 8);

                var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                srcName.header.type = DisplayconfigDeviceInfoGetSourceName;
                srcName.header.size = Marshal.SizeOf(srcName);
                srcName.header.adapterIdLow = lo;
                srcName.header.adapterIdHigh = hi;
                srcName.header.id = src;
                if (DisplayConfigGetDeviceInfo(ref srcName) == 0 &&
                    string.Equals(srcName.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    adapterLow = lo;
                    adapterHigh = hi;
                    sourceId = src;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }

    /// <summary>主显示器的监视器驱动键名（Control\Class\{monitor class}\000X 的最后一段）。</summary>
    private static string? GetPrimaryMonitorSubKey(string gdiDeviceName)
    {
        uint i = 0;
        var adapter = new DISPLAY_DEVICE();
        adapter.cb = Marshal.SizeOf(adapter);
        while (EnumDisplayDevices(null, i++, ref adapter, 0))
        {
            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0 ||
                !string.Equals(adapter.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                adapter.cb = Marshal.SizeOf(adapter);
                continue;
            }

            uint j = 0;
            var monitor = new DISPLAY_DEVICE();
            monitor.cb = Marshal.SizeOf(monitor);
            while (EnumDisplayDevices(gdiDeviceName, j++, ref monitor, 0))
            {
                string key = monitor.DeviceKey;
                if (!string.IsNullOrEmpty(key))
                {
                    int slash = key.LastIndexOf('\\');
                    if (slash >= 0 && slash < key.Length - 1)
                        return key[(slash + 1)..];
                }
                monitor.cb = Marshal.SizeOf(monitor);
            }
            return null;
        }
        return null;
    }

    // ---- P/Invoke ----

    private const int MonitorinfofPrimary = 0x1;
    private const int DisplayDeviceAttachedToDesktop = 0x1;
    private const int DisplayconfigDeviceInfoGetSourceName = 1;
    private const uint ProfileRead = 1;
    private const uint ProfileMemBuffer = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool Monitorenumproc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, Monitorenumproc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public uint adapterIdLow;
        public int adapterIdHigh;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, ref uint numPaths, ref uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, IntPtr topologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME packet);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InstallColorProfileW(string? machine, string profilePath);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetColorDirectoryW(string? machine, [Out] StringBuilder dir, ref uint size);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
    private static extern int ColorProfileSetDisplayDefaultAssociation(
        uint scope, string profileName, uint profileType, uint profileSubType,
        uint adapterIdLow, int adapterIdHigh, uint sourceId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROFILE
    {
        public uint dwType;
        public IntPtr pProfileData;
        public uint cbData;
    }

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenColorProfileW(ref PROFILE profile, uint desiredAccess, uint shareMode, uint creationMode);

    [DllImport("mscms.dll", SetLastError = true)]
    private static extern bool IsColorProfileValid(IntPtr hProfile, out bool valid);

    [DllImport("mscms.dll")]
    private static extern bool CloseColorProfile(IntPtr hProfile);
}

/// <summary>判断 profile 文件是否是有效的显示类（mntr/RGB）ICC：用于"列表中最后一个有效 profile = 默认"的判定。</summary>
internal static class IccProfileFile
{
    public static bool IsDisplayProfile(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length < 128)
                return false;
            var header = new byte[24];
            using var fs = File.OpenRead(fullPath);
            if (fs.Read(header, 0, header.Length) != header.Length)
                return false;
            // 12: 设备类 "mntr"；16: 数据色彩空间 "RGB "
            return ReadFourCc(header, 12) == "mntr" && ReadFourCc(header, 16) == "RGB ";
        }
        catch
        {
            return false;
        }
    }

    private static string ReadFourCc(byte[] b, int off) => Encoding.ASCII.GetString(b, off, 4);
}
