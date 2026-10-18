using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

/// <summary>Active CCD targets. Monitor device paths identify the screen; GDI names/source IDs only locate its current connection.</summary>
internal static class DisplayTopology
{
    internal static Func<IReadOnlyList<IccDisplayInfo>>? Override;
    internal static IReadOnlyList<IccDisplayInfo> GetDisplays() => Override?.Invoke() ?? ReadNative();
    internal static string Identity(IccDisplayInfo display) => display.Identity ?? display.AssociationSubKey;
    internal static IccDisplayInfo Require(string? identity)
    {
        var displays = GetDisplays();
        var matches = displays.Where(d => identity is null ? d.IsPrimary : Identity(d).Equals(identity, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(Str.T("Str.DisplayDisconnected"));
        if (!matches[0].Independent) throw new InvalidOperationException(Str.T("Str.DisplayMirrored"));
        return matches[0];
    }
    internal static void ValidateConnection(IccDisplayInfo captured)
    {
        var current = Require(Identity(captured));
        if (current.GdiDeviceName != captured.GdiDeviceName || current.AdapterIdLow != captured.AdapterIdLow
            || current.AdapterIdHigh != captured.AdapterIdHigh || current.SourceId != captured.SourceId
            || current.AssociationSubKey != captured.AssociationSubKey || current.AdvancedColorEnabled != captured.AdvancedColorEnabled)
            throw new InvalidOperationException(Str.T("Str.DisplayConnectionChanged"));
    }
    internal static IReadOnlyList<IccDisplayInfo> ReadNative()
    {
        // QueryDisplayConfig packs PATH_INFO at 72 bytes, not the allocation's spare capacity.
        for (var retry = 0; retry < 3; retry++)
        {
            uint count = 0, modes = 0;
            if (GetDisplayConfigBufferSizes(2, ref count, ref modes) != 0) return [];
            if (count == 0 || count > 128 || modes > 1024) return [];
            var paths = Marshal.AllocHGlobal(checked((int)count * Marshal.SizeOf<PathInfo>()));
            var modeBuffer = Marshal.AllocHGlobal(checked((int)Math.Max(1, modes) * 64));
            try
            {
                var status = QueryDisplayConfig(2, ref count, paths, ref modes, modeBuffer, IntPtr.Zero);
                if (status == 122) continue; // topology changed since GetBufferSizes
                if (status != 0) return [];
                var activePaths = Enumerable.Range(0, (int)count)
                    .Select(i => Marshal.PtrToStructure<PathInfo>(paths + i * Marshal.SizeOf<PathInfo>())).ToArray();
                var result = new List<IccDisplayInfo>();
                for (var i = 0; i < count; i++)
                {
                    var path = activePaths[i];
                    if (path.Target.Available == 0 || (path.Flags & 1) == 0) continue;
                    var source = new SourceName { Header = Header.For(1, Marshal.SizeOf<SourceName>(), path.Source.Adapter, path.Source.Id) };
                    var target = new TargetName { Header = Header.For(2, Marshal.SizeOf<TargetName>(), path.Target.Adapter, path.Target.Id) };
                    if (GetSourceName(ref source) != 0 || GetTargetName(ref target) != 0 || string.IsNullOrWhiteSpace(target.Path)) continue;
                    var (key, primary) = MonitorKey(source.Name, target.Path);
                    if (key is null) continue;
                    result.Add(new(source.Name, path.Source.Adapter.Low, path.Source.Adapter.High, path.Source.Id, key,
                        MscmsIccApi.UsesPerUserProfiles(key), target.Path.ToUpperInvariant(), target.FriendlyName, primary,
                        activePaths.Count(p => (p.Flags & 1) != 0 && p.Source.Adapter.Low == path.Source.Adapter.Low
                            && p.Source.Adapter.High == path.Source.Adapter.High && p.Source.Id == path.Source.Id) == 1,
                        ReadAdvancedColor(path.Target.Adapter, path.Target.Id)));
                }
                return MarkIndependent(result);
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modeBuffer); }
        }
        return [];
    }
    internal static IReadOnlyList<IccDisplayInfo> MarkIndependent(IReadOnlyList<IccDisplayInfo> displays)
        => displays.Select(d => d with { Independent = d.Independent && displays.Count(other => other.AdapterIdLow == d.AdapterIdLow
            && other.AdapterIdHigh == d.AdapterIdHigh && other.SourceId == d.SourceId) == 1
            && displays.Count(other => Identity(other).Equals(Identity(d), StringComparison.OrdinalIgnoreCase)) == 1
            && displays.Count(other => other.GdiDeviceName.Equals(d.GdiDeviceName, StringComparison.OrdinalIgnoreCase)) == 1
            && displays.Count(other => other.AssociationSubKey == d.AssociationSubKey) == 1 })
            .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.GdiDeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
    private static (string? Key, bool Primary) MonitorKey(string gdi, string targetPath)
    {
        var primary = false;
        for (uint i = 0; i < 128; i++)
        {
            var adapter = Device.Create();
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if (adapter.Name.Equals(gdi, StringComparison.OrdinalIgnoreCase)) { primary = (adapter.Flags & 4) != 0; break; }
        }
        for (uint i = 0; i < 128; i++)
        {
            var monitor = Device.Create();
            if (!EnumDisplayDevices(gdi, i, ref monitor, 1)) break; // EDD_GET_DEVICE_INTERFACE_NAME
            if (!monitor.Id.Equals(targetPath, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(monitor.Key)) continue;
            var key = monitor.Key[(monitor.Key.LastIndexOf('\\') + 1)..];
            if (key.Length == 4 && key.All(char.IsAsciiDigit)) return (key, primary);
        }
        return (null, primary);
    }
    private static bool? ReadAdvancedColor(Luid adapter, uint target)
    {
        var info = new AdvancedColor { Header = Header.For(9, Marshal.SizeOf<AdvancedColor>(), adapter, target) };
        return GetAdvancedColor(ref info) == 0 ? (info.Flags & 2) != 0 : null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColor { public Header Header; public uint Flags, Encoding, Bits; }
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdvancedColor(ref AdvancedColor info);
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Source { public Luid Adapter; public uint Id, Mode, Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Target
    {
        public Luid Adapter; public uint Id, Mode, Technology, Rotation, Scaling, Numerator, Denominator, Scanline;
        public int Available; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Header
    {
        public uint Type, Size; public Luid Adapter; public uint Id;
        public static Header For(uint type, int size, Luid adapter, uint id) => new() { Type = type, Size = (uint)size, Adapter = adapter, Id = id };
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
    { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TargetName
    {
        public Header Header; public uint Flags, Technology; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Device
    {
        public int Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        public static Device Create() => new() { Size = Marshal.SizeOf<Device>() };
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, ref uint paths, ref uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathBuffer, ref uint modes, IntPtr modeBuffer, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SourceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref TargetName name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? gdi, uint index, ref Device device, uint flags);
}
