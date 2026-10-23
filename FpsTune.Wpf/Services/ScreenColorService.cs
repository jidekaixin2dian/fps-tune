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
        IccFilterPreset.Vivid => new(Contrast: 108, Gamma: 1.06, Shadows: 4),
        IccFilterPreset.ShadowBoost => new(Gamma: 1.12, Shadows: 20),
        IccFilterPreset.Dehaze => new(Contrast: 112, Gamma: 1.04, Shadows: 3, Blue: 98),
        IccFilterPreset.NightGuard => new(Gamma: 1.06, Shadows: 8, Green: 97, Blue: 88),
        IccFilterPreset.Warm => new(Contrast: 105, Gamma: 1.02, Green: 98, Blue: 94),
        IccFilterPreset.Cool => new(Contrast: 106, Gamma: 1.04, Red: 96, Green: 99),
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

    internal static void ValidateTransferredBackup(string name, byte[] bytes)
    {
        var r = ParseReceipt(System.Text.Encoding.UTF8.GetString(bytes));
        if (name != Path.GetFileName(ReceiptPath(r.Identity))) throw new InvalidDataException(Str.T("Str.ColorBackupInvalid"));
    }

    private static Receipt ParseReceipt(string json)
    {
        var r = JsonSerializer.Deserialize<Receipt>(json);
        if (r is null || string.IsNullOrWhiteSpace(r.Identity) || r.Identity.Length > 4096 || r.Original?.Length != 768 || r.Expected?.Length != 768
            || r.Previous is not null && r.Previous.Length != 768 || r.Settings is null)
            throw new InvalidDataException(Str.T("Str.ColorBackupInvalid"));
        r.Settings.Validate();
        return r;
    }

    private static Receipt? Load(string id)
    {
        var path = ReceiptPath(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 100000) throw new InvalidDataException(Str.T("Str.ColorBackupLarge"));
        var r = ParseReceipt(File.ReadAllText(path));
        if (r.Identity != id)
            throw new InvalidDataException(Str.T("Str.ColorBackupInvalid"));
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
            return new(true, r is not null && !Owned(r, ramp) ? Str.T("Str.ColorNewBaselineHint") : r is null ? Str.T("Str.ColorChoose") : !Matches(ramp, r.Expected)
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
            var y = MapTone(i / 255d, settings, gains[c]);
            var index = y * 255;
            var lo = (int)index;
            result[c * 256 + i] = (ushort)Math.Round(baseline[c * 256 + lo] * (1 - (index - lo))
                + baseline[c * 256 + Math.Min(255, lo + 1)] * (index - lo));
        }
        return result;
    }
    // Endpoint-preserving curves keep contrast/brightness from flattening a range of
    // shadow values to black. The ICC inverse and preview shader use the same mapping.
    internal static double MapTone(double x, ScreenColorSettings settings, double gain)
    {
        var y = Math.Pow(x, 1 / settings.Gamma);
        y += settings.Shadows / 100 * 4 * y * Math.Pow(1 - y, 2);
        y += (settings.Contrast / 100 - 1) * 2 * y * (1 - y) * (2 * y - 1);
        y += settings.Brightness / 100 * 4 * y * (1 - y);
        return Math.Pow(Math.Clamp(y, 0, 1), 100 / gain);
    }
    public static bool Apply(ScreenColorSettings settings, string id, bool startNewBaselineOnDrift = false, bool preserveIcc = false)
    {
        settings.Validate();
        using var gate = SystemMutationGate.Acquire();
        var display = Require(id);
        // Retire our old managed ICC preset before applying a scan-out effect. Preserve custom/vendor profiles.
        if (!preserveIcc && IccFilterService.HasManagedPreset(id) && IccFilterService.HasRestorableBackup(id))
            IccFilterService.Restore(id);
        display = Require(id);
        var current = Api.Read(display);
        var r = Load(id) ?? new Receipt(id, display.FriendlyName, current, current, new());
        var rebased = false;
        if (!Owned(r, current))
        {
            if (!startNewBaselineOnDrift) throw new InvalidOperationException(Str.T("Str.ColorDrift"));
            DisplayBackupFiles.Archive(ReceiptPath(id));
            r = new(id, display.FriendlyName, current, current, new());
            rebased = true;
        }
        var ramp = BuildRamp(settings, r.Original);
        r = r with { Expected = ramp, Settings = settings, Previous = current };
        Save(r);
        DisplayTopology.ValidateConnection(display);
        Api.Write(display, ramp);
        if (!Matches(Api.Read(display), ramp)) throw new InvalidOperationException(Str.T("Str.ColorRefused"));
        Save(r with { Previous = null });
        return rebased;
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
        return d.ColorMode switch
        {
            DisplayColorMode.Hdr => true,
            DisplayColorMode.Sdr or DisplayColorMode.Wcg => false,
            _ when d.AdvancedColorEnabled == false => false,
            _ => throw new InvalidOperationException(Str.T("Str.ColorHdrUnknown"))
        };
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
