using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>
/// ICC 预设 → 3×256 通道曲线（0–1）。这是「让滤镜在游戏里真正生效」的第一步：
/// 游戏和多数全屏程序不读色彩管理，只有显示器的 gamma ramp 能被它们看到，
/// 所以要把 .icc 里的 rTRC/gTRC/bTRC 曲线取出来，和画面参数一起合成进 ramp。
/// 只处理我们自有预设实际用到的 <c>curv</c>（TABLE/lut8-16）与 <c>para</c>（1–4 型）；
/// 3D LUT（mft1/mft2）与色度矩阵不在这一层的表达范围内，遇到时按恒等曲线处理并标记。
/// </summary>
public sealed record IccCurves(double[][] Channels, bool HasTable, bool HasParametric, bool Unsupported)
{
    /// <summary>是否是一条什么都不做的曲线（三通道全恒等）。</summary>
    public bool IsIdentity => !HasTable && !HasParametric || Channels.All(IsIdentityChannel);

    private static bool IsIdentityChannel(double[] channel)
        => channel.Length == 256 && Enumerable.Range(0, 256).All(i => Math.Abs(channel[i] - i / 255d) < 1e-6);

    public static IccCurves FromCurves(double[][] channels, bool table, bool para, bool unsupported = false)
        => new(channels, table, para, unsupported);

    /// <summary>恒等曲线（没有滤镜时用）。</summary>
    public static IccCurves Identity { get; } = new(
    [
        Enumerable.Range(0, 256).Select(i => i / 255d).ToArray(),
        Enumerable.Range(0, 256).Select(i => i / 255d).ToArray(),
        Enumerable.Range(0, 256).Select(i => i / 255d).ToArray()
    ], false, false, false);
}

/// <summary>从 .icc 字节里读 rTRC/gTRC/bTRC，采样成 256 点曲线（纯函数，可单测）。</summary>
public static class IccCurveReader
{
    /// <summary>解析失败时返回 null（调用方按「没有滤镜」处理，不阻断写入）。</summary>
    public static IccCurves? TryRead(byte[]? data)
    {
        if (data is null || data.Length < 132) return null;
        try
        {
            var tagCount = ReadUInt32(data, 128);
            if (tagCount is <= 0 or > 256) return null;
            long offset = 132;
            if (offset + tagCount * 12L > data.Length) return null;
            var channels = new double[3][];
            var table = false; var para = false; var unsupported = false;
            var tags = new[] { "rTRC", "gTRC", "bTRC" };
            for (var c = 0; c < 3; c++)
            {
                channels[c] = Enumerable.Range(0, 256).Select(i => i / 255d).ToArray();
                for (var t = 0; t < tagCount; t++)
                {
                    var record = 132 + t * 12;
                    if (!Matches(data, record, tags[c])) continue;
                    var tagOffset = (int)ReadUInt32(data, record + 4);
                    var tagSize = (int)ReadUInt32(data, record + 8);
                    if (tagOffset < 0 || tagSize <= 0 || tagOffset + (long)tagSize > data.Length) { unsupported = true; break; }
                    var curve = ReadCurve(data, tagOffset, tagSize);
                    if (curve is null) { unsupported = true; break; }
                    channels[c] = curve.Value.Curve;
                    table |= curve.Value.Table; para |= curve.Value.Parametric;
                    break;
                }
            }
            return IccCurves.FromCurves(channels, table, para, unsupported);
        }
        catch { return null; }
    }

    /// <summary>读一个曲线 tag：返回 256 点采样；curv 表按线性插值，para 按 ICC 规范公式。</summary>
    private static (double[] Curve, bool Table, bool Parametric)? ReadCurve(byte[] data, int offset, int size)
    {
        if (size < 12) return null;
        if (Matches(data, offset, "curv"))
        {
            var count = (int)ReadUInt32(data, offset + 8);
            if (count == 0) return (Identity(), false, false);
            if (count == 1)
            {
                if (offset + 14 > data.Length) return null;
                var gamma = ReadUInt16(data, offset + 12) / 256d;
                return (Enumerable.Range(0, 256).Select(i => Math.Pow(i / 255d, gamma)).ToArray(), false, false);
            }
            if (count < 2 || count > 4096 || offset + 12L + count * 2L > data.Length) return null;
            var table = new double[count];
            for (var i = 0; i < count; i++) table[i] = ReadUInt16(data, offset + 12 + i * 2) / 65535d;
            return (SampleTable(table), true, false);
        }
        if (Matches(data, offset, "para"))
        {
            if (size < 16) return null;
            var type = ReadUInt16(data, offset + 8);
            // 各型参数个数：0 型 1 个、1 型 3 个、2 型 4 个、3 型 5 个、4 型 7 个（不是 type+1）。
            var count = type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => 0 };
            if (count == 0) return null;
            var p = new double[count];
            if (offset + 12 + p.Length * 4L > data.Length) return null;
            for (var i = 0; i < p.Length; i++) p[i] = ReadS15Fixed16(data, offset + 12 + i * 4);
            var curve = new double[256];
            for (var i = 0; i < 256; i++) curve[i] = Math.Clamp(Parametric(type, p, i / 255d), 0, 1);
            return (curve, false, true);
        }
        return null;
    }

    /// <summary>ICC para 曲线 0–4 型（typed curve）；参数越界时按端点夹紧。</summary>
    private static double Parametric(int type, double[] p, double x) => type switch
    {
        0 => Math.Pow(x, p[0]),
        1 => Math.Pow(Math.Max(0, p[1] * x + p[2]), p[0]),
        2 => Math.Pow(Math.Max(0, p[1] * x + p[2]), p[0]) + p[3],
        3 => x >= p[4] ? Math.Pow(Math.Max(0, p[1] * x + p[2]), p[0]) : p[3] * x,
        4 => x >= p[4] ? Math.Pow(Math.Max(0, p[1] * x + p[2]), p[0]) + p[5] : p[3] * x + p[6],
        _ => x
    };

    private static double[] SampleTable(double[] table)
    {
        var curve = new double[256];
        for (var i = 0; i < 256; i++)
        {
            var position = i / 255d * (table.Length - 1);
            var lo = (int)Math.Floor(position);
            var hi = Math.Min(table.Length - 1, lo + 1);
            curve[i] = table[lo] + (table[hi] - table[lo]) * (position - lo);
        }
        return curve;
    }

    private static double[] Identity() => Enumerable.Range(0, 256).Select(i => i / 255d).ToArray();

    private static bool Matches(byte[] data, int offset, string signature)
        => offset + 4 <= data.Length && data[offset] == signature[0] && data[offset + 1] == signature[1]
            && data[offset + 2] == signature[2] && data[offset + 3] == signature[3];

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static double ReadS15Fixed16(byte[] data, int offset)
        => (int)ReadUInt32(data, offset) / 65536d;
}
