using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>
/// ICC 预设 → 3×256 通道曲线（0–1）。
///
/// 关键区别（踩过一次坑）：曲线有两种语义，只有一种是「编码值→编码值」的 ramp：
/// - <c>vcgt</c> 校准表：显示器校准 ramp，输入输出都是屏幕编码值，可以**直接**折进 gamma ramp；
/// - <c>rTRC/gTRC/bTRC</c>：ICC 规定的**显示响应曲线**（编码值→线性光）。我们自己生成的预设里存的是
///   <c>MapTone</c> 的反函数，把它当 ramp 用会与画面参数里的 <c>MapTone</c> 抵消，
///   所有预设最后都变成同一条「sRGB 解码」曲线（画面整体发暗、预设之间毫无区别）。
///   所以 TRC 只登记不折入，<see cref="EncodedRamp"/> 为 false。
///
/// 只处理 <c>curv</c>（TABLE/lut8-16）与 <c>para</c>（1–4 型）；3D LUT（mft1/mft2）与色度矩阵不在这一层，
/// 遇到时按恒等曲线处理并标记。
/// </summary>
public sealed record IccCurves(double[][] Channels, bool HasTable, bool HasParametric, bool Unsupported, bool EncodedRamp = false)
{
    /// <summary>是否是一条什么都不做的曲线（三通道全恒等）。</summary>
    public bool IsIdentity => !HasTable && !HasParametric || Channels.All(IsIdentityChannel);

    private static bool IsIdentityChannel(double[] channel)
        => channel.Length == 256 && Enumerable.Range(0, 256).All(i => Math.Abs(channel[i] - i / 255d) < 1e-6);

    public static IccCurves FromCurves(double[][] channels, bool table, bool para, bool unsupported = false, bool encodedRamp = false)
        => new(channels, table, para, unsupported, encodedRamp);

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
            // vcgt 是显示器校准 ramp（预设真正的"效果"所在），有它就优先用它：
            // rTRC/gTRC/bTRC 只是色彩转换曲线，观感差异远小于 vcgt。
            var vcgt = ReadVcgt(data, tagCount);
            // 只有 vcgt 是「编码值→编码值」的 ramp，可以直接折进屏幕 gamma；
            // TRC 是「编码值→线性」的显示响应，折进去只会与画面参数互相抵消（见类型注释）。
            if (vcgt is not null) return IccCurves.FromCurves(vcgt, true, false, encodedRamp: true);
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

    /// <summary>
    /// vcgt 校准 tag（type 0）。真实文件的布局（Windows 自带的、1U 的预设、显示器厂商的都一样）：
    /// <c>sig(4) + reserved(4) + type(4) + channels(u16) + entries(u16)</c> 共 16 字节头，
    /// 后面是 <c>entries</c> 个「R,G,B 交织」的 u16 三元组（256 条 → 1536 字节，整条 tag 1552–1554 字节）。
    /// 之前按「12 字节头 + 257 条按通道平铺」解析，于是所有真实校准表都被判成异常而丢弃——
    /// 这就是「在 Windows 里换 ICC 没反应」的直接原因。
    /// 老的 12 字节头/平铺布局仍兼容（我们 0.4.1 早期写出的文件是那种）。
    /// </summary>
    private static double[][]? ReadVcgt(byte[] data, uint tagCount)
    {
        for (var t = 0; t < tagCount; t++)
        {
            var record = 132 + t * 12;
            if (!Matches(data, record, "vcgt")) continue;
            var offset = (int)ReadUInt32(data, record + 4);
            var size = (int)ReadUInt32(data, record + 8);
            if (offset < 0 || size < 12 || offset + (long)size > data.Length) return null;
            var type = ReadUInt32(data, offset + 8);
            if (type != 0) return null;

            // 16 字节头：channels/entries 明确写出来，数据是交织三元组。
            if (size >= 16)
            {
                var channelsField = ReadUInt16(data, offset + 12);
                var entriesField = ReadUInt16(data, offset + 14);
                if (channelsField == 3 && entriesField is >= 2 and <= 1024
                    && offset + 16L + entriesField * 3L * 2L <= data.Length + 2)
                    return ReadInterleaved(data, offset + 16, entriesField);
            }

            // 老布局：12 字节头 + 按通道平铺。
            var entries = (size - 12) / 6;
            if (entries < 2 || offset + 12L + entries * 3L * 2L > data.Length) return null;
            var planar = new double[3][];
            for (var c = 0; c < 3; c++)
            {
                var raw = new double[entries];
                for (var i = 0; i < entries; i++)
                    raw[i] = ReadUInt16(data, offset + 12 + (c * entries + i) * 2) / 65535d;
                planar[c] = SampleTable(raw);
            }
            return Validate(planar) ? planar : null;
        }
        return null;
    }

    /// <summary>交织三元组：第 i 个三元组是 (R,G,B) 的第 i 个采样。</summary>
    private static double[][]? ReadInterleaved(byte[] data, int start, int entries)
    {
        var channels = new[] { new double[entries], new double[entries], new double[entries] };
        for (var i = 0; i < entries; i++)
            for (var c = 0; c < 3; c++)
                channels[c][i] = ReadUInt16(data, start + (i * 3 + c) * 2) / 65535d;
        if (!Validate(channels.Select(SampleTable).ToArray())) return null;
        return channels.Select(SampleTable).ToArray();
    }

    /// <summary>端点与单调性：量化 + 真实测量会有极小回折，容差按 1/255 的十分之一给。</summary>
    private static bool Validate(double[][] channels)
    {
        foreach (var channel in channels)
        {
            if (channel[0] > 0.1 || channel[^1] < 0.85) return false;
            for (var i = 1; i < channel.Length; i++)
                if (channel[i] < channel[i - 1] - 1e-4) return false;
        }
        return true;
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
