using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record ScreenColorSettings(double Brightness = 0, double Contrast = 100, double Gamma = 1,
    double Shadows = 0, double Red = 100, double Green = 100, double Blue = 100, double Highlight = 0,
    double DarkBalance = ScreenColorService.DefaultDarkBalance,
    // 游戏加加是「开关 + 滑杆」：关着时这条曲线完全不参与，滑杆值只记住不用。
    bool DarkBalanceEnabled = false)
{
    public void Validate()
    {
        var values = new[] { Brightness, Contrast, Gamma, Shadows, Red, Green, Blue, Highlight, DarkBalance };
        if (values.Any(v => !double.IsFinite(v)) || Brightness is < -20 or > 20 || Contrast is < 80 or > 120
            || Gamma is < .7 or > 1.5 || Shadows is < 0 or > 25 || Highlight is < -25 or > 25 || DarkBalance is < 0 or > 100
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
        IccFilterPreset.Deexposure => new(Highlight: -14),
        IccFilterPreset.DeexposureStrong => new(Highlight: -22, Shadows: 3),
        IccFilterPreset.Neutral => new(),
        // 以下 21 条按 1U 2.9.6 的明文调色参数换算（详见 work/research/1u-filter-gap/），
        // Brightness/Contrast/Shadows/Red/Blue 已按 mappingRules 折算到本产品的取值范围。
        IccFilterPreset.Daylight => new(Brightness: -6, Contrast: 101.6, Gamma: 1.351, Shadows: 14.5),
        IccFilterPreset.Bright => new(Brightness: -2.8, Contrast: 96, Gamma: 1.25, Shadows: 3.5, Red: 100.5, Blue: 99.5),
        IccFilterPreset.VividLively => new(Brightness: 2, Contrast: 104.4, Gamma: .943, Shadows: 11.5, Red: 100.8, Blue: 99.2),
        IccFilterPreset.Cinema => new(Brightness: 6, Contrast: 99.6, Gamma: .98, Shadows: 6.5, Red: 100.5, Blue: 99.5),
        IccFilterPreset.NightScene => new(Brightness: -8, Contrast: 112, Gamma: 1.5, Shadows: 11.2, Red: 100.5, Blue: 99.5),
        IccFilterPreset.Foggy => new(Brightness: -3.2, Contrast: 104.4, Gamma: 1.5, Shadows: 1.8),
        IccFilterPreset.SoftBright => new(Brightness: -7.6, Contrast: 103.2, Gamma: 1.351, Shadows: 2.5, Red: 100.5, Blue: 99.5),
        IccFilterPreset.ClearBright => new(Brightness: 4.8, Contrast: 98, Gamma: .962, Shadows: 3.5, Red: 100.5, Blue: 99.5),
        IccFilterPreset.ArenaCool => new(Contrast: 107.2, Shadows: 15, Red: 95, Blue: 105),
        IccFilterPreset.Lively => new(Brightness: 6.4, Contrast: 99.2, Gamma: 1.042, Shadows: 3.5),
        IccFilterPreset.ClearBright2 => new(Brightness: 2.4, Contrast: 98.4, Gamma: 1.163, Shadows: 3, Highlight: -16, Red: 99.8, Blue: 100.2),
        IccFilterPreset.GameGenshin => new(Brightness: -6.4, Contrast: 110.4, Gamma: 1.136, Shadows: 14.8),
        IccFilterPreset.GameKarakin => new(Brightness: -4, Contrast: 98, Gamma: 1.5),
        IccFilterPreset.GameGw2 => new(Brightness: -4, Contrast: 101.6, Gamma: 1.5, Red: 99.9, Blue: 100.1),
        IccFilterPreset.GameApex => new(Brightness: -2, Contrast: 101.2, Gamma: 1.19, Shadows: .8),
        IccFilterPreset.GameNaraka => new(Brightness: -4.8, Contrast: 97.2, Gamma: 1.136, Shadows: 1.5),
        IccFilterPreset.GameMyth => new(Brightness: -1.2, Contrast: 100, Gamma: 1.19, Shadows: 1, Red: 99.9, Blue: 100.1),
        IccFilterPreset.GameR6 => new(Brightness: 3.6, Contrast: 102.8, Gamma: 1.111, Shadows: 7.8, Red: 100.6, Blue: 99.4),
        IccFilterPreset.GameValorant => new(Brightness: -5.2, Contrast: 110, Gamma: 1.5, Shadows: 8.8),
        IccFilterPreset.GameNaraka2 => new(Brightness: -9.2, Contrast: 96.4, Gamma: 1.316, Shadows: .8),
        IccFilterPreset.GameCod16 => new(Brightness: -2.8, Contrast: 101.6, Gamma: 1.5, Shadows: 2),
        // 会员调色（1U PRO_PRESET_TUNES 原值折算）：brightness/contrast 按 (v-50)*0.4、gamma 按 50/v、
        // shadow 按 v*0.25、highlights 按 (v-50)*0.5（0 视为不调）、色温按 (kelvin-6500)/4000 折 R/B。
        IccFilterPreset.VipSaturated => new(Brightness: 0.8, Contrast: 104.4, Gamma: 1.5, Highlight: -6, Red: 98.6, Blue: 101.4),
        IccFilterPreset.VipDeepVivid => new(Contrast: 98.4, Gamma: 0.806, Red: 99.25, Blue: 100.75),
        IccFilterPreset.VipWarmClear => new(Brightness: -1.6, Contrast: 102.8, Gamma: 1.5, Shadows: 2, Red: 100.6, Blue: 99.4),
        IccFilterPreset.VipCoolSuppress => new(Brightness: 2, Contrast: 101.2, Gamma: 1.25, Highlight: -3.5, Red: 99.55, Blue: 100.45),
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
    /// <summary>暗部平衡的中性值：45 对应 γ=1.0（不改变画面）；游戏加加自己的默认档 50 是 γ=0.9。</summary>
    /// <summary>游戏加加 `DefinitionEnhancedDarkValue` 默认 50（对应 γ≈0.9）；关掉开关时这条曲线不参与。</summary>
    internal const double DefaultDarkBalance = 50;
    /// <summary>曲线不改变画面时的滑杆位置（γ=1.0 落在 36–37 之间，游戏加加中点也不是中性）。</summary>
    internal const double NeutralDarkBalance = 37;

    /// <summary>
    /// 游戏加加「暗部平衡」的原始映射（逆向自 DisplayColorControl.dll / HardwareAddon.node，两处常量一致）：
    /// v = trunc(p/100*11 + 26) ∈ [26,37]，γ = (40-v)/10 ∈ [1.4, 0.3]，只有 12 档。
    /// </summary>
    internal static double DarkBalanceGamma(double darkBalance)
    {
        var steps = (int)(Math.Clamp(darkBalance, 0, 100) / 100 * 11 + 26);
        return (40 - steps) / 10d;
    }

    internal static ushort[] BuildRamp(ScreenColorSettings settings, ushort[] baseline, IccCurves? filter = null)
    {
        settings.Validate();
        if (baseline.Length != 768) throw new InvalidDataException(Str.T("Str.ColorLength"));
        var result = new ushort[768];
        var gains = new[] { settings.Red, settings.Green, settings.Blue };
        // 开关关着时按中性值参与，曲线整体退化为恒等（不要每像素 with 一次）。
        var effective = settings.DarkBalanceEnabled ? settings : settings with { DarkBalance = NeutralDarkBalance };
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < 256; i++)
        {
            var y = MapTone(i / 255d, effective, gains[c]);
            // 只有 vcgt 校准表（编码值→编码值的 ramp）才折进屏幕曲线。
            // TRC 是 ICC 的「显示响应」曲线（编码值→线性光），我们生成预设时存的是 MapTone 的反函数：
            // 折进来会与上面的 MapTone 抵消，所有预设最后都等于同一条 sRGB 解码曲线——
            // 表现就是「换了滤镜看着一样、整体还发暗」。预设各自的观感由画面参数（MapTone）承担。
            if (filter is { EncodedRamp: true }) y = SampleCurve(filter.Channels[c], y);
            var index = y * 255;
            var lo = (int)index;
            result[c * 256 + i] = (ushort)Math.Round(baseline[c * 256 + lo] * (1 - (index - lo))
                + baseline[c * 256 + Math.Min(255, lo + 1)] * (index - lo));
        }
        return result;
    }
    /// <summary>按 256 点表线性插值取曲线值（滤镜曲线与预览共用同一套采样）。</summary>
    internal static double SampleCurve(double[] curve, double x)
    {
        if (curve.Length < 2) return x;
        var position = Math.Clamp(x, 0, 1) * 255;
        var lo = (int)Math.Floor(position);
        var hi = Math.Min(255, lo + 1);
        return curve[lo] + (curve[hi] - curve[lo]) * (position - lo);
    }

    // Endpoint-preserving curves keep contrast/brightness from flattening a range of
    // shadow values to black. The ICC inverse and preview shader use the same mapping.
    internal static double MapTone(double x, ScreenColorSettings settings, double gain)
    {
        // 暗部平衡=游戏加加「DefinitionEnhancedDark」的等比幂律曲线：输入先上抬 1/256（黑位不落到 0，
        // 这就是"暗部平衡"的观感来源），再按 12 档 γ 做幂运算；γ=1 时与改造前完全等价。
        // 开关关着时按中性值参与——BuildRamp 曾经在调用前替换过 settings，但 MapTone 也被
        // IccProfileGenerator 等直接调用，那些路径会按滑杆值算出一条「黑位被抬起」的曲线，
        // 与实际写屏用的中性曲线不一致（黑位发灰、ICC 与屏幕对不上）。所以判断放进这里一处。
        var darkGamma = DarkBalanceGamma(settings.DarkBalanceEnabled ? settings.DarkBalance : NeutralDarkBalance);
        var input = darkGamma == 1 ? x : Math.Clamp(x + 1 / 256d, 0, 1);
        var y = Math.Pow(input, darkGamma / settings.Gamma);
        y += settings.Shadows / 100 * 4 * y * Math.Pow(1 - y, 2);
        y += (settings.Contrast / 100 - 1) * 2 * y * (1 - y) * (2 * y - 1);
        y += settings.Brightness / 100 * 4 * y * (1 - y);
        // 高光窗（smoothstep 0.5→1，端点保持）：负值压高光（去曝光），正值提亮白点。
        var w = y <= .5 ? 0 : (y - .5) * 2;
        w = w * w * (3 - 2 * w);
        y += settings.Highlight / 100 * w * y * (1 - y) * 2;
        return Math.Pow(Math.Clamp(y, 0, 1), 100 / gain);
    }
    public static bool Apply(ScreenColorSettings settings, string id, bool startNewBaselineOnDrift = false, bool preserveIcc = false,
        IccCurves? filter = null)
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
        var ramp = BuildRamp(settings, r.Original, filter);
        r = r with { Expected = ramp, Settings = settings, Previous = current };
        Save(r);
        DisplayTopology.ValidateConnection(display);
        Api.Write(display, ramp);
        if (!Matches(Api.Read(display), ramp)) throw new InvalidOperationException(Str.T("Str.ColorRefused"));
        Save(r with { Previous = null });
        return rebased;
    }
    /// <summary>
    /// 写完后守一段时间：改数字振动后显卡会异步重建颜色管线，把刚写的 gamma 顶回原始值
    /// （真机实测：写入后约 0.5–1s 被抹平，用户看到的就是"闪一下就没"）。
    /// 这里逐次读回，漂了就补写；全部尝试后仍不一致返回 false，交给调用方如实报告。
    /// </summary>
    public static bool EnsureRampSticks(string id, int attempts = 5, int delayMs = 400)
    {
        var stable = false;
        for (var i = 0; i < attempts; i++)
        {
            Thread.Sleep(delayMs);
            using var gate = SystemMutationGate.Acquire();
            var r = Load(id);
            if (r is null) return false;
            var display = Require(id);
            if (!Matches(Api.Read(display), r.Expected))
            {
                Api.Write(display, r.Expected);
                stable = Matches(Api.Read(display), r.Expected);
            }
            else stable = true;
        }
        return stable;
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
