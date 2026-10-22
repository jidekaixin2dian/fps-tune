using System.IO;
using System.Text;

namespace FpsTune.Wpf.Services;

/// <summary>ICC 滤镜内置预设。标准 sRGB 不在此列——它是还原目标（恢复备份的原关联），不是生成的 profile。</summary>
public enum IccFilterPreset
{
    Vivid = 0,       // FPS 鲜艳：微增饱和与对比
    ShadowBoost = 1, // 暗部增强：阴影段 gamma 上抬
    Dehaze = 2,      // 去雾：S 型对比曲线 + 微降蓝
    NightGuard = 3,  // 夜战护眼：明显降蓝 + 暗部微抬（夜间/长时间）
    Warm = 4,        // 暖色：中度降蓝 + 对比微升
    Cool = 5,        // 冷色清晰：微升蓝 + 对比升
    Soft = 6,        // 柔和：降饱和 + 对比微降（久看不累）
    ShadowGentle = 7,
    ContrastGentle = 8,
    Muted = 9,
    Neutral = 10,
}

/// <summary>
/// 程序化生成最小 ICC v2 显示 profile（RGB 矩阵 + gamma/曲线 tag）的纯函数实现：
/// 输入预设参数，输出完整 byte[]，不落盘、不碰系统。格式经真机 mscms IsColorProfileValid 验证。
/// 诚实边界：这是简单曲线变换，效果弱于专业校色；对色准敏感的用户应保留原 profile。
/// </summary>
public static class IccProfileGenerator
{
    public sealed record PresetParams(string FileName, string Description)
    {
        /// <summary>sRGB 原色 → PCS D50（Bradford 适配）矩阵列，ICC 官方 sRGB 剖面数值。</summary>
        public double[] WXyz { get; init; } = { 0.9642, 1.0000, 0.8249 };
        public double[] RXyz { get; init; } = { 0.4360, 0.2225, 0.0139 };
        public double[] GXyz { get; init; } = { 0.3851, 0.7169, 0.0971 };
        public double[] BXyz { get; init; } = { 0.1431, 0.0606, 0.7141 };

        /// <summary>设备编码(0..1) → 线性亮度的 TRC 曲线查找表。空 = 使用固定 gamma 2.2 单值曲线。</summary>
        public ushort[]? Curve { get; init; }
        public ushort[]? RedCurve { get; init; }
        public ushort[]? GreenCurve { get; init; }
        public ushort[]? BlueCurve { get; init; }
    }

    // Bump this suffix when changing preset transforms; keep active older profiles intact.
    internal static string FileNameFor(IccFilterPreset preset) => $"FpsTune-{preset}-v3.icc";

    public static PresetParams ParamsFor(IccFilterPreset preset)
    {
        var settings = ScreenColorSettings.ForPreset(preset);
        var saturation = preset switch { IccFilterPreset.Vivid => 1.06, IccFilterPreset.Soft => .94, IccFilterPreset.Muted => .96, _ => 1d };
        // A destination profile is inverted by the CMM: store the inverse desired
        // saturation matrix and inverse desired display curves, preserving D50 white.
        double[] Column(double[] primary, double luminance) => Enumerable.Range(0, 3)
            .Select(i => primary[i] / saturation + (1 - 1 / saturation) * luminance * (StdRXyz[i] + StdGXyz[i] + StdBXyz[i])).ToArray();
        return new(FileNameFor(preset), $"FPS Tune - {preset} - shadow-preserving v3")
        {
            RXyz = Column(StdRXyz, .2126), GXyz = Column(StdGXyz, .7152), BXyz = Column(StdBXyz, .0722),
            RedCurve = InverseDisplayCurve(settings, settings.Red),
            GreenCurve = InverseDisplayCurve(settings, settings.Green),
            BlueCurve = InverseDisplayCurve(settings, settings.Blue),
        };
    }

    internal static (string Title, string Description) ResourceKeysFor(IccFilterPreset preset) => preset switch
    {
        IccFilterPreset.Vivid => ("Str.IccVivid", "Str.IccVividDesc"),
        IccFilterPreset.ShadowBoost => ("Str.IccShadow", "Str.IccShadowDesc"),
        IccFilterPreset.Dehaze => ("Str.IccDehaze", "Str.IccDehazeDesc"),
        IccFilterPreset.NightGuard => ("Str.IccNight", "Str.IccNightDesc"),
        IccFilterPreset.Warm => ("Str.IccWarm", "Str.IccWarmDesc"),
        IccFilterPreset.Cool => ("Str.IccCool", "Str.IccCoolDesc"),
        IccFilterPreset.Soft => ("Str.IccSoft", "Str.IccSoftDesc"),
        IccFilterPreset.ShadowGentle => ("Str.IccShadowGentle", "Str.IccShadowGentleDesc"),
        IccFilterPreset.ContrastGentle => ("Str.IccContrastGentle", "Str.IccContrastGentleDesc"),
        IccFilterPreset.Muted => ("Str.IccMuted", "Str.IccMutedDesc"),
        IccFilterPreset.Neutral => ("Str.IccNeutral", "Str.IccNeutralDesc"),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    private static readonly double[] StdRXyz = { 0.4360, 0.2225, 0.0139 };
    private static readonly double[] StdGXyz = { 0.3851, 0.7169, 0.0971 };
    private static readonly double[] StdBXyz = { 0.1431, 0.0606, 0.7141 };

    private static ushort[] InverseDisplayCurve(ScreenColorSettings settings, double gain)
    {
        const int count = 1024;
        var curve = new ushort[count];
        for (var i = 1; i < count - 1; i++)
        {
            var output = i / (count - 1d);
            double low = 0, high = 1;
            for (var step = 0; step < 28; step++)
            {
                var mid = (low + high) / 2;
                if (ScreenColorService.MapTone(mid, settings, gain) < output) low = mid; else high = mid;
            }
            var input = (low + high) / 2;
            // Exact sRGB EOTF, including the linear toe, avoids a second shadow shift.
            var linear = input <= .04045 ? input / 12.92 : Math.Pow((input + .055) / 1.055, 2.4);
            curve[i] = ToU16Fixed(linear);
        }
        curve[^1] = ushort.MaxValue;
        return curve;
    }

    private static ushort ToU16Fixed(double v) => (ushort)Math.Clamp(Math.Round(v * 65535.0), 0, 65535);

    /// <summary>生成完整 ICC v2 字节流。纯函数：相同参数输出逐字节相同（日期字段固定，不取当前时间）。</summary>
    public static byte[] Build(IccFilterPreset preset) => Build(ParamsFor(preset));

    public static byte[] Build(PresetParams p)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", DescTag(p.Description)),
            ("wtpt", XyzTag(p.WXyz)),
            ("rXYZ", XyzTag(p.RXyz)),
            ("gXYZ", XyzTag(p.GXyz)),
            ("bXYZ", XyzTag(p.BXyz)),
            ("rTRC", CurveTag(p.RedCurve ?? p.Curve, 2.2)),
            ("gTRC", CurveTag(p.GreenCurve ?? p.Curve, 2.2)),
            ("bTRC", CurveTag(p.BlueCurve ?? p.Curve, 2.2)),
            ("cprt", Encoding.ASCII.GetBytes("text\0\0\0\0" + "FpsTune\0")),
        };

        int tagTableSize = 4 + tags.Count * 12;
        int cursor = 128 + tagTableSize;
        var offsets = new List<(string Sig, int Offset, int Size)>();
        foreach (var (sig, data) in tags)
        {
            offsets.Add((sig, cursor, data.Length));
            cursor += (data.Length + 3) & ~3; // tag 数据 4 字节对齐
        }
        int total = cursor;

        using var ms = new MemoryStream(total);
        var bw = new BinaryWriter(ms);

        // 头部 128 字节，大端
        bw.Write(Be32((uint)total));           // 0 尺寸
        bw.Write(Be32(0));                     // 4 首选 CMM
        bw.Write(Be32(0x02100000));            // 8 版本 2.1
        bw.Write(FourCC("mntr"));              // 12 设备类
        bw.Write(FourCC("RGB "));              // 16 数据色彩空间
        bw.Write(FourCC("XYZ "));              // 20 PCS
        bw.Write(Be16(2026)); bw.Write(Be16(9)); bw.Write(Be16(1));   // 24 创建日期（固定，保证纯函数）
        bw.Write(Be16(0)); bw.Write(Be16(0)); bw.Write(Be16(0));
        bw.Write(FourCC("acsp"));              // 36 签名
        bw.Write(Be32(0));                     // 40 平台
        bw.Write(Be32(0));                     // 44 标志
        bw.Write(Be32(0));                     // 48 制造商
        bw.Write(Be32(0));                     // 52 型号
        bw.Write(Be64(0));                     // 56 属性
        bw.Write(Be32(0));                     // 64 渲染意图
        bw.Write(Be32(0x0000F6D6)); bw.Write(Be32(0x00010000)); bw.Write(Be32(0x0000D32D)); // 68 PCS 白点 D50
        bw.Write(Be32(0));                     // 80 创建者
        bw.Write(new byte[16]);                // 84 profile id
        bw.Write(new byte[28]);                // 100 保留

        bw.Write(Be32((uint)tags.Count));
        foreach (var (sig, off, size) in offsets)
        {
            bw.Write(FourCC(sig));
            bw.Write(Be32((uint)off));
            bw.Write(Be32((uint)size));
        }
        foreach (var (_, data) in tags)
        {
            bw.Write(data);
            while (ms.Length % 4 != 0) bw.Write((byte)0);
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] DescTag(string text)
    {
        // v2 textDescriptionType：ASCII + unicode + scriptcode
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var unicode = Encoding.BigEndianUnicode.GetBytes(text + "\0");
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(FourCC("desc"));
        bw.Write(Be32(0));
        bw.Write(Be32((uint)ascii.Length));
        bw.Write(ascii);
        bw.Write(Be32(0x656E5553));               // unicode 语言码 "enUS"
        bw.Write(Be32((uint)(text.Length + 1)));  // unicode 字符数（含 \0）
        bw.Write(unicode);
        bw.Write(Be16(0));                        // scriptcode code
        bw.Write((byte)0);                        // scriptcode count
        bw.Write(new byte[67]);                   // scriptcode 描述区
        return ms.ToArray();
    }

    private static byte[] XyzTag(double[] xyz)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(FourCC("XYZ "));
        bw.Write(Be32(0));
        foreach (var v in xyz) bw.Write(Be32((uint)Math.Round(v * 65536.0)));
        return ms.ToArray();
    }

    private static byte[] CurveTag(ushort[]? lut, double fallbackGamma)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(FourCC("curv"));
        bw.Write(Be32(0));
        if (lut is null)
        {
            bw.Write(Be32(1)); // 单值 = u16Fixed16 gamma
            bw.Write(Be16((ushort)Math.Round(fallbackGamma * 256.0))); // ICC curveType uses u8Fixed8, not s15Fixed16.
        }
        else
        {
            bw.Write(Be32((uint)lut.Length));
            foreach (var v in lut) bw.Write(Be16(v));
        }
        return ms.ToArray();
    }

    private static byte[] Be32(uint v) => BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((int)v));
    private static byte[] Be16(ushort v) => BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v));
    private static byte[] Be64(ulong v) => BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((long)v));
    private static byte[] FourCC(string s) => Encoding.ASCII.GetBytes(s);
}
