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
/// Profile installation/validation uses mscms. Per-user associations use the physical monitor's documented WCS store.
/// Parent GDI/adapter APIs can select an inactive cached monitor; never use them to write a selected physical screen.
/// </summary>
internal interface IIccSystemApi
{
    /// <summary>兼容读取入口：定位当前主显示器。</summary>
    bool TryGetPrimaryDisplay(out IccDisplayInfo display);
    IReadOnlyList<IccDisplayInfo> GetDisplays() => TryGetPrimaryDisplay(out var display) ? [display] : [];
    void ValidateConnection(IccDisplayInfo display) { }
    bool CanRestoreAbsentDefault => false;
    string? ReadDefaultProfile(IccDisplayInfo display)
    {
        var directory = TryGetColorDirectory();
        return directory is null ? null : ReadAssociationList(display).Reverse()
            .FirstOrDefault(name => IccProfileFile.IsDisplayProfile(Path.Combine(directory, name)));
    }

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
    // An empty per-user list is a restorable Windows-default state.
    // Writing [] removes our associations without inventing an original profile.
    public bool CanRestoreAbsentDefault => true;

    private const string AssociationBasePath =
        @"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{4d36e96e-e325-11ce-bfc1-08002be10318}";
    public IReadOnlyList<IccDisplayInfo> GetDisplays() => DisplayTopology.GetDisplays();
    public void ValidateConnection(IccDisplayInfo display) => DisplayTopology.ValidateConnection(display);

    public bool TryGetPrimaryDisplay(out IccDisplayInfo display)
    {
        display = GetDisplays().SingleOrDefault(d => d.IsPrimary)!;
        return display is not null;
    }
    internal static bool UsesPerUserProfiles(string keyName)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AssociationBasePath + "\\" + keyName);
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
        var profiles = ReadAssociationList(display).Where(n => !n.Equals(profileName, StringComparison.OrdinalIgnoreCase)).ToList();
        profiles.Add(profileName);
        WriteAssociationList(display, profiles.ToArray());
        return true;
    }
    public string[] ReadAssociationList(IccDisplayInfo display)
    {
        ValidateConnection(display);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AssociationBasePath + "\\" + display.AssociationSubKey);
        return key?.GetValue("ICMProfile") as string[] ?? [];
    }
    public void WriteAssociationList(IccDisplayInfo display, string[] list)
    {
        ValidateConnection(display);
        if (!UsesPerUserProfiles(display.AssociationSubKey)) throw new InvalidOperationException(Str.T("Str.BackupTargetChanged"));
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AssociationBasePath + "\\" + display.AssociationSubKey);
        key.SetValue("ICMProfile", list, Microsoft.Win32.RegistryValueKind.MultiString);
        // Existing color-managed applications observe the documented registry change. Notify the desktop too.
        SendMessageTimeout(new IntPtr(0xffff), 0x1a, UIntPtr.Zero, "ICM", 2, 200, out _);
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

    internal static string? DisplayIdentity(string gdiDeviceName, string? monitorIdentity = null)
    {
        uint index = 0;
        if (monitorIdentity is not null)
        {
            var found = false;
            for (; index < 128; index++)
            {
                var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(gdiDeviceName, index, ref monitor, 1)) break;
                if (monitor.DeviceID.Equals(monitorIdentity, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            }
            if (!found) return null;
        }
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevices(gdiDeviceName, index, ref device, 0) || string.IsNullOrWhiteSpace(device.DeviceID)
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

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
