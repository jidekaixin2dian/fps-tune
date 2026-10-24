using System.Runtime.InteropServices;
using System.Text;

namespace FpsTune.Wpf.Services;

internal interface INvidiaDisplayModes
{
    void ProbeCustom(IccDisplayInfo display, DisplayMode mode);
    void TryCustom(IccDisplayInfo display, DisplayMode mode);
    void SaveCustom(IccDisplayInfo display);
    void RevertTrial(IccDisplayInfo display);
    uint ReadScaling(IccDisplayInfo display);
    void SetScaling(IccDisplayInfo display, uint scaling, bool testOnly = false);
}

/// <summary>Public R590+ NVAPI ABI; reuse the installed driver, including its timing generator.</summary>
internal sealed class NvidiaDisplayModes : INvidiaDisplayModes
{
    private readonly NvapiNative.QueryInterfaceDelegate _query;
    internal NvidiaDisplayModes()
    {
        if (!NvapiNative.TryLoad(out _query, out var error)) throw new InvalidOperationException(error);
        Check(_query.Resolve<NvapiNative.InitializeDelegate>(NvapiNative.IdInitialize)(), Str.T("Str.Resolution.NvidiaDisplayModes.1"));
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DisplayId([MarshalAs(UnmanagedType.LPStr)] string name, out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ErrorMessage(int status, StringBuilder message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTiming(uint id, ref TimingInput input, out Timing timing);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Custom([In] uint[] ids, uint count, ref CustomDisplay mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DisplayAction([In] uint[] ids, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Save([In] uint[] ids, uint count, uint outputOnly, uint monitorOnly);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetConfig(ref uint count, IntPtr paths);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetConfig(uint count, IntPtr paths, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct TimingInput
    { public uint Version, Width, Height; public float Hz; public uint Flags, Tv, Scaling, Type; }
    [StructLayout(LayoutKind.Sequential)] internal struct Timing
    {
        public ushort HVisible, HBorder, HFront, HSync, HTotal; public byte HPol;
        public ushort VVisible, VBorder, VFront, VSync, VTotal; public byte VPol;
        public ushort Interlaced; public uint PixelClock, Flag; public ushort Refresh;
        public uint Refresh1K, Aspect; public ushort Repeat; public uint Status;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)] public byte[] Name;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct CustomDisplay
    {
        public uint Version, Width, Height, Depth, Format;
        public float Left, Top, Right, Bottom, XRatio, YRatio;
        public Timing Timing; public uint HardwareOnly;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo
    { public uint Version, SourceId, Count; public IntPtr Targets, Source; public uint Flags; public IntPtr Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct TargetInfo { public uint Id; public IntPtr Details; public uint TargetId; }
    [StructLayout(LayoutKind.Sequential)] private struct Advanced
    { public uint Version, Rotation, Scaling, Refresh1K, Flags, Connector, Tv, TimingType; public Timing Timing; }
    private static uint Version<T>(uint v = 1) => (uint)Marshal.SizeOf<T>() | (v << 16);
    private uint Id(IccDisplayInfo display)
    {
        DisplayTopology.ValidateConnection(display);
        Check(_query.Resolve<DisplayId>(0xae457190)(display.GdiDeviceName, out var id), Str.T("Str.Resolution.NvidiaDisplayModes.2"));
        if (id == 0) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.3"));
        return id;
    }
    private void Check(int result, string operation)
    {
        if (result == 0) return;
        var message = new StringBuilder(64);
        _query.Resolve<ErrorMessage>(0x6c2d048c)(result, message);
        throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.4", operation, message, result));
    }
    private CustomDisplay Build(IccDisplayInfo display, DisplayMode mode)
    {
        mode.Validate();
        var input = new TimingInput { Version = Version<TimingInput>(), Width = (uint)mode.Width, Height = (uint)mode.Height, Hz = mode.Hz, Type = 1 };
        Check(_query.Resolve<GetTiming>(0x175167e9)(Id(display), ref input, out var timing), Str.T("Str.Resolution.NvidiaDisplayModes.5"));
        // AUTO may return native scan-out dimensions when the GPU scales a smaller source surface.
        // NV_CUSTOM_DISPLAY.width/height describe the requested source, not the backend timing.
        if (timing.HVisible == 0 || timing.VVisible == 0 || timing.HTotal < timing.HVisible || timing.VTotal < timing.VVisible || timing.Interlaced != 0
            || timing.PixelClock == 0 || Math.Abs(timing.Refresh1K / 1000.0 - mode.Hz) > .6)
            throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.6"));
        return new() { Version = Version<CustomDisplay>(), Width = (uint)mode.Width, Height = (uint)mode.Height,
            Depth = (uint)mode.Bits, Right = 1, Bottom = 1, XRatio = 1, YRatio = 1, Timing = timing };
    }
    public void ProbeCustom(IccDisplayInfo display, DisplayMode mode)
    {
        if (mode.Orientation != 0) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.7"));
        _ = _query.Resolve<Custom>(0x1f7db630); _ = _query.Resolve<Save>(0x49882876);
        _ = _query.Resolve<DisplayAction>(0xcbbd40f0);
        _ = Build(display, mode); // Calculation is not a hardware compatibility test.
    }
    public void TryCustom(IccDisplayInfo display, DisplayMode mode)
    {
        var custom = Build(display, mode);
        Check(_query.Resolve<Custom>(0x1f7db630)([Id(display)], 1, ref custom), Str.T("Str.Resolution.NvidiaDisplayModes.8"));
    }
    public void SaveCustom(IccDisplayInfo display) => Check(_query.Resolve<Save>(0x49882876)([Id(display)], 1, 1, 1), Str.T("Str.Resolution.NvidiaDisplayModes.9"));
    public void RevertTrial(IccDisplayInfo display) => Check(_query.Resolve<DisplayAction>(0xcbbd40f0)([Id(display)], 1), Str.T("Str.Resolution.NvidiaDisplayModes.10"));

    // Read all paths; edit only the matching target on a fresh topology. Never replay another screen's saved path array.
    private sealed class Config : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];
        internal IntPtr Paths;
        internal uint Count;
        internal IntPtr Allocate(int bytes)
        {
            var p = Marshal.AllocHGlobal(bytes); _allocations.Add(p);
            Marshal.Copy(new byte[bytes], 0, p, bytes); return p;
        }
        public void Dispose() { foreach (var p in _allocations) Marshal.FreeHGlobal(p); }
    }
    private Config ReadConfig()
    {
        var config = new Config();
        try
        {
            var get = _query.Resolve<GetConfig>(0x11abccf8);
            uint count = 0; Check(get(ref count, IntPtr.Zero), Str.T("Str.Resolution.NvidiaDisplayModes.11"));
            if (count is 0 or > 128) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.12"));
            config.Count = count; config.Paths = config.Allocate((int)count * Marshal.SizeOf<PathInfo>());
            for (var i = 0; i < count; i++)
                Marshal.StructureToPtr(new PathInfo { Version = Version<PathInfo>(2), Source = config.Allocate(32), Adapter = config.Allocate(8) },
                    config.Paths + i * Marshal.SizeOf<PathInfo>(), false);
            Check(get(ref count, config.Paths), Str.T("Str.Resolution.NvidiaDisplayModes.13"));
            if (count != config.Count) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.14"));
            for (var i = 0; i < count; i++)
            {
                var p = config.Paths + i * Marshal.SizeOf<PathInfo>(); var path = Marshal.PtrToStructure<PathInfo>(p);
                if (path.Count is 0 or > 128) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.15"));
                path.Targets = config.Allocate((int)path.Count * Marshal.SizeOf<TargetInfo>());
                for (var j = 0; j < path.Count; j++)
                {
                    var details = config.Allocate(Marshal.SizeOf<Advanced>());
                    Marshal.StructureToPtr(new Advanced { Version = Version<Advanced>(), Timing = new Timing { Name = new byte[40] } }, details, false);
                    Marshal.StructureToPtr(new TargetInfo { Details = details }, path.Targets + j * Marshal.SizeOf<TargetInfo>(), false);
                }
                Marshal.StructureToPtr(path, p, false);
            }
            Check(get(ref count, config.Paths), Str.T("Str.Resolution.NvidiaDisplayModes.16"));
            if (count != config.Count) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.17"));
            return config;
        }
        catch { config.Dispose(); throw; }
    }
    private static IntPtr Find(Config config, uint id)
    {
        var matches = new List<IntPtr>();
        for (var i = 0; i < config.Count; i++)
        {
            var path = Marshal.PtrToStructure<PathInfo>(config.Paths + i * Marshal.SizeOf<PathInfo>());
            if ((path.Flags & 1) != 0) continue;
            for (var j = 0; j < path.Count; j++)
            {
                var target = Marshal.PtrToStructure<TargetInfo>(path.Targets + j * Marshal.SizeOf<TargetInfo>());
                if (target.Id != id) continue;
                if (path.Count != 1 || target.Details == IntPtr.Zero) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.18"));
                matches.Add(target.Details);
            }
        }
        if (matches.Count != 1) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.19"));
        return matches[0];
    }
    public uint ReadScaling(IccDisplayInfo display)
    {
        var id = Id(display); using var config = ReadConfig();
        return Marshal.PtrToStructure<Advanced>(Find(config, id)).Scaling;
    }
    public void SetScaling(IccDisplayInfo display, uint scaling, bool testOnly = false)
    {
        if (scaling is not (1 or 2 or 3 or 5 or 6 or 7 or 8)) throw new InvalidOperationException(Str.T("Str.Resolution.NvidiaDisplayModes.20"));
        var id = Id(display); using var config = ReadConfig(); var p = Find(config, id);
        var details = Marshal.PtrToStructure<Advanced>(p); details.Scaling = scaling;
        Marshal.StructureToPtr(details, p, false);
        DisplayTopology.ValidateConnection(display);
        Check(_query.Resolve<SetConfig>(0x5d8cf8de)(config.Count, config.Paths, testOnly ? 1u : 0u), testOnly ? Str.T("Str.Resolution.NvidiaDisplayModes.21") : Str.T("Str.Resolution.NvidiaDisplayModes.22"));
    }
}
