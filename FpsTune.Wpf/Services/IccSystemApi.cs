using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FpsTune.Wpf.Services;

/// <summary>显示器身份与当前连接：GDI 名、LUID/source、关联键和 per-user 状态。</summary>
public sealed record IccDisplayInfo(
    string GdiDeviceName,
    uint AdapterIdLow,
    int AdapterIdHigh,
    uint SourceId,
    string AssociationSubKey,
    bool UsePerUserProfiles,
    string? Identity = null, string FriendlyName = "", bool IsPrimary = true, bool Independent = true);

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
    /// <summary>兼容读取入口：定位当前主显示器。</summary>
    bool TryGetPrimaryDisplay(out IccDisplayInfo display);
    IReadOnlyList<IccDisplayInfo> GetDisplays() => TryGetPrimaryDisplay(out var display) ? [display] : [];
    void ValidateConnection(IccDisplayInfo display) { }

    /// <summary>系统色彩目录（COLOR directory）；失败返回 null。</summary>
    string? TryGetColorDirectory();

    bool InstallColorProfile(string sourcePath);

    /// <summary>把 profile 追加为当前用户显示关联列表的默认（列表末尾）。</summary>
    bool SetDisplayDefaultAssociation(IccDisplayInfo display, string profileName);

    string[] ReadAssociationList(IccDisplayInfo display);

    /// <summary>精确写回选定显示器的 per-user 关联列表，写前复核连接。</summary>
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
    private const uint WcsScopeCurrentUser = 1;
    private const uint ProfileTypeIcm = 0;
    private const uint ProfileSubTypeNone = 4; // CPT_ICM + CPST_NONE = 设备默认 ICC

    public IReadOnlyList<IccDisplayInfo> GetDisplays() => DisplayTopology.GetDisplays();
    public void ValidateConnection(IccDisplayInfo display) => DisplayTopology.ValidateConnection(display);

    public bool TryGetPrimaryDisplay(out IccDisplayInfo display)
    {
        display = GetDisplays().SingleOrDefault(d => d.IsPrimary)!;
        return display is not null;
    }
    internal static bool UsesPerUserProfiles(string subKey)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AssociationBasePath + "\\" + subKey);
        return key?.GetValue("UsePerUserProfiles") is not int value || value != 0;
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
    {
        ValidateConnection(display);
        return ColorProfileSetDisplayDefaultAssociation(
            WcsScopeCurrentUser, profileName, ProfileTypeIcm, ProfileSubTypeNone,
            display.AdapterIdLow, display.AdapterIdHigh, display.SourceId) == 0;
    }

    public string[] ReadAssociationList(IccDisplayInfo display)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AssociationBasePath + "\\" + display.AssociationSubKey);
        return (key?.GetValue("ICMProfile") as string[]) ?? Array.Empty<string>();
    }

    public void WriteAssociationList(IccDisplayInfo display, string[] list)
    {
        ValidateConnection(display);
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

    internal static string? DisplayIdentity(string gdiDeviceName)
    {
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevices(gdiDeviceName, 0, ref device, 0) || string.IsNullOrWhiteSpace(device.DeviceID)
            || string.IsNullOrWhiteSpace(device.DeviceKey)) return null;
        return device.DeviceID.ToUpperInvariant() + "|" + device.DeviceKey.ToUpperInvariant();
    }

    private const uint ProfileRead = 1;
    private const uint ProfileMemBuffer = 2;

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
