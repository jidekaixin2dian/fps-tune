using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

internal sealed record DisplayMode(int Width, int Height, int Hz, int Bits = 32, int Orientation = 0,
    int Flags = 0, int X = 0, int Y = 0)
{
    public override string ToString() => $"{Width} × {Height} · {Hz} Hz";
    internal void Validate()
    {
        if (Width is < 320 or > 16384 || Height is < 200 or > 16384 || Hz is < 24 or > 1000
            || Bits is not (16 or 24 or 32) || Orientation is < 0 or > 3 || (Flags & ~6) != 0
            || Math.Abs((long)X) > 65536 || Math.Abs((long)Y) > 65536)
            throw new InvalidOperationException(Str.T("Str.Resolution.DisplayModeApi.1"));
    }
}

internal interface IDisplayModeApi
{
    DisplayMode Read(IccDisplayInfo display);
    IReadOnlyList<DisplayMode> Modes(IccDisplayInfo display);
    void Test(IccDisplayInfo display, DisplayMode mode);
    void Apply(IccDisplayInfo display, DisplayMode mode);
}

/// <summary>Use only driver-enumerated modes. CDS_TEST never creates an NVIDIA custom mode.</summary>
internal sealed class WindowsDisplayModeApi : IDisplayModeApi
{
    // wingdi.h DEVMODEW (display union). No printer fields or variable driver payload.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    internal struct DevMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(76)] public int X;
        [FieldOffset(80)] public int Y;
        [FieldOffset(84)] public int Orientation;
        [FieldOffset(88)] public int FixedOutput;
        [FieldOffset(168)] public int Bits;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
        [FieldOffset(180)] public int Flags;
        [FieldOffset(184)] public int Hz;
        internal DisplayMode Mode => new(Width, Height, Hz, Bits, Orientation, Flags, X, Y);
    }
    private static DevMode Get(string device, int index)
    {
        var dm = new DevMode { Size = 220 };
        if (!EnumDisplaySettingsEx(device, index, ref dm, 0)) throw new InvalidOperationException(Str.T("Str.Resolution.DisplayModeApi.2"));
        return dm;
    }
    public DisplayMode Read(IccDisplayInfo display) => Get(display.GdiDeviceName, -1).Mode;
    public IReadOnlyList<DisplayMode> Modes(IccDisplayInfo display)
    {
        var result = new List<DisplayMode>();
        var current = Read(display);
        for (var i = 0; i < 16384; i++)
        {
            var dm = new DevMode { Size = 220 };
            if (!EnumDisplaySettingsEx(display.GdiDeviceName, i, ref dm, 0)) break;
            // Preserve the desktop location; do not rotate or introduce interlaced modes.
            if (dm.Bits != current.Bits || dm.Orientation != current.Orientation || (dm.Flags & 2) != 0 || dm.Hz < 24) continue;
            result.Add(dm.Mode with { X = current.X, Y = current.Y });
        }
        return result.Distinct().OrderBy(m => m.Width).ThenBy(m => m.Height).ThenByDescending(m => m.Hz).ToArray();
    }
    public void Test(IccDisplayInfo display, DisplayMode mode) => Change(display, mode, 2);
    public void Apply(IccDisplayInfo display, DisplayMode mode) => Change(display, mode, 0);
    private static void Change(IccDisplayInfo display, DisplayMode mode, uint flags)
    {
        mode.Validate();
        var dm = Get(display.GdiDeviceName, -1);
        dm.Fields = 0x20 | 0x80 | 0x40000 | 0x80000 | 0x100000 | 0x200000 | 0x400000;
        dm.Width = mode.Width; dm.Height = mode.Height; dm.Hz = mode.Hz; dm.Bits = mode.Bits;
        dm.X = mode.X; dm.Y = mode.Y; dm.Orientation = mode.Orientation; dm.Flags = mode.Flags;
        var result = ChangeDisplaySettingsEx(display.GdiDeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException(result switch
        {
            -2 => Str.T("Str.Resolution.DisplayModeApi.3"), -3 => Str.T("Str.Resolution.DisplayModeApi.4"),
            -5 => Str.T("Str.Resolution.DisplayModeApi.5"), 1 => Str.T("Str.Resolution.DisplayModeApi.6"),
            _ => Str.T("Str.Resolution.DisplayModeApi.7", result)
        });
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string device, int index, ref DevMode mode, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr parameter);
}
