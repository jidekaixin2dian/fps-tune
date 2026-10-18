using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record ScreenColorSettings(double Brightness = 0, double Contrast = 100, double Gamma = 1,
    double Shadows = 0, double Red = 100, double Green = 100, double Blue = 100)
{
    public void Validate()
    {
        var values = new[] { Brightness, Contrast, Gamma, Shadows, Red, Green, Blue };
        if (values.Any(v => !double.IsFinite(v)) || Brightness is < -20 or > 20 || Contrast is < 80 or > 120
            || Gamma is < .7 or > 1.5 || Shadows is < 0 or > 25
            || new[] { Red, Green, Blue }.Any(v => v is < 80 or > 110))
            throw new ArgumentOutOfRangeException(nameof(ScreenColorSettings), Str.T("Str.ColorOutOfRange"));
    }
    public static ScreenColorSettings ForPreset(IccFilterPreset preset) => preset switch
    {
        IccFilterPreset.Vivid => new(Contrast: 112, Gamma: 1.04),
        IccFilterPreset.ShadowBoost => new(Gamma: 1.12, Shadows: 20),
        IccFilterPreset.Dehaze => new(Contrast: 118, Blue: 97),
        IccFilterPreset.NightGuard => new(Gamma: 1.06, Shadows: 8, Green: 95, Blue: 82),
        IccFilterPreset.Warm => new(Contrast: 105, Green: 98, Blue: 91),
        IccFilterPreset.Cool => new(Contrast: 108, Red: 92, Green: 97),
        IccFilterPreset.Soft => new(Contrast: 90, Gamma: 1.02, Blue: 97),
        IccFilterPreset.ShadowGentle => new(Shadows: 8),
        IccFilterPreset.ContrastGentle => new(Contrast: 106),
        IccFilterPreset.Muted => new(Contrast: 95, Red: 96, Green: 96, Blue: 96),
        IccFilterPreset.Neutral => new(),
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };
    public static string TitleFor(IccFilterPreset preset) => preset switch
    {
        IccFilterPreset.Vivid => Str.T("Str.ColorVivid"), IccFilterPreset.Muted => Str.T("Str.ColorMuted"),
        _ => Str.T(IccProfileGenerator.ResourceKeysFor(preset).Title)
    };
}

internal interface IScreenGammaApi
{
    bool IsHdr(IccDisplayInfo display);
    ushort[] Read(IccDisplayInfo display);
    void Write(IccDisplayInfo display, ushort[] ramp);
}

public sealed record ScreenColorState(bool Supported, string Detail, ScreenColorSettings Settings, bool Restorable);

/// <summary>SDR scan-out calibration. Keep the physical monitor identity and original ramp, never infer effect from an ICC association.</summary>
public static class ScreenColorService
{
    internal static IScreenGammaApi? ApiOverride;
    internal static string? BackupDirOverride;
    private static IScreenGammaApi Api => ApiOverride ?? new ScreenGammaApi();
    private static string DirectoryPath => BackupDirOverride ?? Path.Combine(UserDataPaths.Root, "screen-color");
    private static string ReceiptPath(string id) => DisplayBackupFiles.PathFor(DirectoryPath, "color", id);
    private sealed record Receipt(string Identity, string DisplayName, ushort[] Original, ushort[] Expected,
        ScreenColorSettings Settings, ushort[]? Previous = null);

    private static Receipt? Load(string id)
    {
        var path = ReceiptPath(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 100000) throw new InvalidDataException(Str.T("Str.ColorBackupLarge"));
        var r = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (r is null || r.Identity != id || r.Original?.Length != 768 || r.Expected?.Length != 768
            || r.Previous is not null && r.Previous.Length != 768 || r.Settings is null)
            throw new InvalidDataException(Str.T("Str.ColorBackupInvalid"));
        r.Settings.Validate();
        return r;
    }
    private static void Save(Receipt r) => AtomicFile.WriteAllTextDurable(ReceiptPath(r.Identity), JsonSerializer.Serialize(r), new System.Text.UTF8Encoding(false));
    internal static bool Matches(ushort[] a, ushort[] b) => a.Length == 768 && b.Length == 768
        && a.Zip(b).All(v => Math.Abs((int)v.First - v.Second) <= 257); // Hardware may quantize to 8 bits.
    private static bool Owned(Receipt r, ushort[] current) => Matches(current, r.Original) || Matches(current, r.Expected)
        || r.Previous is not null && Matches(current, r.Previous);
    private static IccDisplayInfo Require(string id)
    {
        var display = DisplayTopology.Require(id);
        DisplayTopology.ValidateConnection(display);
        if (Api.IsHdr(display)) throw new InvalidOperationException(Str.T("Str.ColorHdr"));
        return display;
    }
    public static ScreenColorState GetState(string id)
    {
        try
        {
            var display = Require(id);
            var ramp = Api.Read(display);
            var r = Load(id);
            return new(true, r is null ? Str.T("Str.ColorChoose") : !Matches(ramp, r.Expected)
                ? Str.T("Str.ColorOverwritten") : Str.T("Str.ColorVerified"),
                r?.Settings ?? new(), r is not null);
        }
        catch (Exception ex) { return new(false, ex.Message, new(), File.Exists(ReceiptPath(id))); }
    }
    internal static ushort[] BuildRamp(ScreenColorSettings settings, ushort[] baseline)
    {
        settings.Validate();
        if (baseline.Length != 768) throw new InvalidDataException(Str.T("Str.ColorLength"));
        var result = new ushort[768];
        var gains = new[] { settings.Red, settings.Green, settings.Blue };
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255d;
            var y = Math.Pow(x, 1 / settings.Gamma);
            y += settings.Shadows / 100 * 4 * x * Math.Pow(1 - x, 2);
            y = Math.Clamp(((y - .5) * settings.Contrast / 100 + .5 + settings.Brightness / 100) * gains[c] / 100, 0, 1);
            var index = y * 255;
            var lo = (int)index;
            result[c * 256 + i] = (ushort)Math.Round(baseline[c * 256 + lo] * (1 - (index - lo))
                + baseline[c * 256 + Math.Min(255, lo + 1)] * (index - lo));
        }
        return result;
    }
    public static void Apply(ScreenColorSettings settings, string id)
    {
        settings.Validate();
        using var gate = SystemMutationGate.Acquire();
        var display = Require(id);
        // Retire our old managed ICC preset before applying a scan-out effect. Preserve custom/vendor profiles.
        var icc = IccFilterService.GetState(id);
        if (icc.Restorable && icc.CurrentProfileName is { } name
            && IccFilterService.AllPresetFileNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            IccFilterService.Restore(id);
        display = Require(id);
        var current = Api.Read(display);
        var r = Load(id) ?? new Receipt(id, display.FriendlyName, current, current, new());
        if (!Owned(r, current)) throw new InvalidOperationException(Str.T("Str.ColorDrift"));
        var ramp = BuildRamp(settings, r.Original);
        r = r with { Expected = ramp, Settings = settings, Previous = current };
        Save(r);
        DisplayTopology.ValidateConnection(display);
        Api.Write(display, ramp);
        if (!Matches(Api.Read(display), ramp)) throw new InvalidOperationException(Str.T("Str.ColorRefused"));
        Save(r with { Previous = null });
    }
    public static bool Restore(string id)
    {
        using var gate = SystemMutationGate.Acquire();
        var r = Load(id);
        if (r is null) return false;
        var display = Require(id);
        if (!Owned(r, Api.Read(display))) throw new InvalidOperationException(Str.T("Str.ColorRestoreDrift"));
        Api.Write(display, r.Original);
        if (!Matches(Api.Read(display), r.Original)) throw new InvalidOperationException(Str.T("Str.ColorRestoreFailed"));
        File.Delete(ReceiptPath(id));
        return true;
    }
    internal static IReadOnlyList<DisplayBackupTarget> BackupTargets() => DisplayBackupFiles.List(DirectoryPath, "color").Select(path =>
    {
        try
        {
            var r = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            if (Path.GetFullPath(path) != Path.GetFullPath(ReceiptPath(r.Identity))) throw new InvalidDataException();
            _ = Load(r.Identity);
            return new DisplayBackupTarget(r.Identity, r.DisplayName);
        }
        catch (Exception ex) { return new DisplayBackupTarget(null, Path.GetFileName(path), ex.Message); }
    }).ToArray();
}

internal sealed class ScreenGammaApi : IScreenGammaApi
{
    public bool IsHdr(IccDisplayInfo d)
    {
        return d.AdvancedColorEnabled ?? throw new InvalidOperationException(Str.T("Str.ColorHdrUnknown"));
    }
    public ushort[] Read(IccDisplayInfo d)
    {
        DisplayTopology.ValidateConnection(d);
        var dc = CreateDC("DISPLAY", d.GdiDeviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException(Str.T("Str.ColorOpenFailed"));
        try
        {
            var ramp = new ushort[768];
            if (!GetDeviceGammaRamp(dc, ramp) || ramp.All(v => v == 0)) throw new InvalidOperationException(Str.T("Str.ColorReadFailed"));
            return ramp;
        }
        finally { DeleteDC(dc); }
    }
    public void Write(IccDisplayInfo d, ushort[] ramp)
    {
        DisplayTopology.ValidateConnection(d);
        var dc = CreateDC("DISPLAY", d.GdiDeviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException(Str.T("Str.ColorOpenFailed"));
        try { if (!SetDeviceGammaRamp(dc, ramp)) throw new InvalidOperationException(Str.T("Str.ColorWriteFailed")); }
        finally { DeleteDC(dc); }
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, [In] ushort[] ramp);
}
