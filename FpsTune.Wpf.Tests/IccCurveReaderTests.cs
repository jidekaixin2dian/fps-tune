using System.Text;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// ICC 曲线解析：这是「让滤镜在游戏里生效」的基础数据层。
/// 内置 43 个 .icc + 生成式预设都必须能解析出 3×256 曲线，且绝大多数不是恒等曲线，
/// 否则合进 gamma ramp 之后仍然看不出效果。
/// </summary>
public sealed class IccCurveReaderTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "catalog", "catalog.json"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!;
        }
        throw new InvalidOperationException("未定位到仓库根");
    }

    private static IEnumerable<(string Name, byte[] Bytes)> BuiltInPresets()
    {
        var dir = Path.Combine(RepoRoot(), "FpsTune.Wpf", "Assets", "GameFilters");
        foreach (var file in Directory.GetFiles(dir, "*.icc").OrderBy(f => f, StringComparer.Ordinal))
            yield return (Path.GetFileName(file), File.ReadAllBytes(file));
    }

    [Fact]
    public void Every_builtin_preset_parses_into_three_sampled_curves()
    {
        var presets = BuiltInPresets().ToArray();
        Assert.True(presets.Length >= 40, $"内置预设数量异常：{presets.Length}");
        var nonIdentity = 0;
        foreach (var (name, bytes) in presets)
        {
            var curves = IccCurveReader.TryRead(bytes);
            Assert.True(curves is not null, $"{name} 解析返回 null（长度 {bytes.Length}）");
            Assert.False(curves!.Unsupported, $"{name} 有无法解析的曲线 tag");
            Assert.Equal(3, curves.Channels.Length);
            foreach (var channel in curves.Channels)
            {
                Assert.Equal(256, channel.Length);
                Assert.All(channel, v => Assert.InRange(v, 0, 1));
                // vcgt 校准表端点允许不满量（校准后常被裁到 0.85–1.0），TRC 曲线则应当到 1。
                Assert.InRange(channel[0], 0, 0.1);
                Assert.InRange(channel[255], 0.85, 1.0);
            }
            if (!curves.IsIdentity) nonIdentity++;
        }
        // 绝大多数内置预设本身就改变曲线；恒等的应当是极少数（例如原色类）。
        Assert.True(nonIdentity >= presets.Length - 3, $"只有 {nonIdentity}/{presets.Length} 条预设改变了曲线");
    }

    [Fact]
    public void Generated_presets_carry_a_vcgt_calibration_table_like_1u()
    {
        var parsed = 0;
        foreach (var preset in Enum.GetValues<IccFilterPreset>())
        {
            var icc = IccProfileGenerator.Build(preset);
            var curves = IccCurveReader.TryRead(icc);
            Assert.NotNull(curves);
            Assert.All(curves!.Channels, channel => Assert.Equal(256, channel.Length));
            // 结构对齐参考实现：预设自带 vcgt 校准表（3×257 u16 = 1554 字节），观感就在这条表里。
            Assert.True(ContainsTag(icc, "vcgt"), $"{preset} 的剖面里没有 vcgt");
            // 16 字节头 + 3×257 条按通道平铺 = 1558 字节（真实 Windows/参考实现文件的实测布局）
            Assert.Equal(1558, VcgtSize(icc));
            Assert.True(curves.EncodedRamp, $"{preset} 的曲线应当来自 vcgt（编码值→编码值）");
            if (!curves.IsIdentity) parsed++;
        }
        Assert.True(parsed >= 8, $"生成式预设里只有 {parsed} 条改变了曲线");
    }

    [Fact]
    public void Reads_the_real_world_vcgt_layout()
    {
        // 真实文件的实测布局（Windows 自带、参考实现预设、厂商 profile 都一样）：
        // 16 字节头（sig + reserved + type + channels + entries），后面按通道平铺、每通道 257 条；
        // 而且真实测量会有极小回折。以前按「12 字节头 + 按通道平铺 257」硬算，边界错位 → 全部被判成异常丢弃。
        static double Curve(double x) => Math.Pow(x, 1.1);
        const int entries = 257;
        var payload = new List<byte>();
        payload.AddRange(Encoding.ASCII.GetBytes("vcgt"));
        payload.AddRange(new byte[4]);                   // reserved
        payload.AddRange(new byte[4]);                   // type 0
        payload.AddRange(new byte[] { 0, 3 });            // channels = 3
        payload.AddRange(new byte[] { 1, 1 });            // entries = 257
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < entries; i++)
        {
            var v = Math.Clamp(Curve(i / (entries - 1d)), 0, 1);
            var raw = (ushort)Math.Round(v * 65535);
            if (i == 100 && c == 0) raw -= 8;             // 模拟量化/测量的微小回折
            payload.Add((byte)(raw >> 8));
            payload.Add((byte)raw);
        }
        var icc = new byte[128 + 4 + 12 + payload.Count];
        icc[128 + 3] = 1;                                 // tagCount = 1
        Encoding.ASCII.GetBytes("vcgt").CopyTo(icc, 132);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(144)).CopyTo(icc, 136);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(payload.Count)).CopyTo(icc, 140);
        payload.ToArray().CopyTo(icc, 144);

        var curves = IccCurveReader.TryRead(icc);
        Assert.NotNull(curves);
        Assert.True(curves!.EncodedRamp);
        Assert.InRange(curves.Channels[0][0], 0, 0.01);
        Assert.InRange(curves.Channels[0][255], 0.99, 1.0);
        Assert.InRange(curves.Channels[0][128], Curve(128 / 256d) - 0.02, Curve(128 / 256d) + 0.02);
    }

    private static bool ContainsTag(byte[] icc, string signature) => TagSize(icc, signature) > 0;

    private static int VcgtSize(byte[] icc) => TagSize(icc, "vcgt");

    private static int TagSize(byte[] icc, string signature)
    {
        var count = ReadBe32(icc, 128);
        for (var t = 0; t < count; t++)
        {
            var record = 132 + t * 12;
            if (System.Text.Encoding.ASCII.GetString(icc, record, 4) != signature) continue;
            return ReadBe32(icc, record + 8);
        }
        return 0;
    }

    private static int ReadBe32(byte[] data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    [Fact]
    public void Broken_or_foreign_bytes_are_rejected_instead_of_throwing()
    {
        Assert.Null(IccCurveReader.TryRead(null));
        Assert.Null(IccCurveReader.TryRead(Array.Empty<byte>()));
        Assert.Null(IccCurveReader.TryRead(new byte[200]));
        var garbage = Enumerable.Range(0, 512).Select(i => (byte)(i * 7)).ToArray();
        Assert.Null(IccCurveReader.TryRead(garbage));
    }

    [Fact]
    public void Curve_math_is_monotonic_for_brightness_style_presets()
    {
        // 「柔和/提亮」类预设曲线应当单调不降：否则合进 ramp 会出现色带反折。
        var strong = IccCurveReader.TryRead(IccProfileGenerator.Build(IccFilterPreset.Vivid));
        Assert.NotNull(strong);
        foreach (var channel in strong!.Channels)
            for (var i = 1; i < 256; i++)
                Assert.True(channel[i] >= channel[i - 1] - 1e-6, $"第 {i} 点出现回折");
    }

    [Fact]
    public void Standard_four_param_type3_trc_parses_within_tag_bounds()
    {
        // 回归：旧读端把 3 型 para 按 5 参数读，参数边界又只对整个文件校验——标准 sRGB 的
        // 3 型 TRC 恰好 12+16 字节，第 5 个「参数」读到了 tag 外的字节，整条曲线落进线性分支。
        // 规范 3 型 = (g,a,b,d)，X<d 时 Y=0。
        var tag = ParaTag(3, 2.4, 1 / 1.055, 0.055 / 1.055, 0.04045);
        var curves = IccCurveReader.TryRead(ProfileWithParaTag(tag));
        Assert.NotNull(curves);
        Assert.False(curves!.Unsupported);
        var channel = curves.Channels[0];
        Assert.Equal(0.0, channel[10], 6);  // 0.0392 < d=0.04045 → 线性分支按规范为 0
        Assert.True(channel[13] > 0, "X≥d 后应进入幂函数分支");
        Assert.Equal(Srgb(128 / 255d), channel[128], 6);
    }

    [Fact]
    public void Legacy_five_param_type3_still_evaluates_linear_branch()
    {
        // 本应用旧版生成的「3 型 + 5 参数」编码（语义即 4 型的 cX 分支）：旧文件必须照常可读。
        var tag = ParaTag(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045);
        var curves = IccCurveReader.TryRead(ProfileWithParaTag(tag));
        Assert.NotNull(curves);
        Assert.False(curves!.Unsupported);
        var channel = curves.Channels[0];
        Assert.Equal(5 / 255d / 12.92, channel[5], 6); // 暗部线性：cX 而不是 0
        Assert.Equal(Srgb(128 / 255d), channel[128], 6);
    }

    [Fact]
    public void Generated_presets_use_spec_conformant_type4_trc_and_roundtrip()
    {
        var (tag, _) = ExtractTag(IccProfileGenerator.Build(IccFilterPreset.Vivid), "rTRC");
        // 4 型（7 参数）规范编码；旧版是 3 型 + 5 参数，第三方解析器会读错暗部。
        Assert.Equal(12 + 7 * 4, tag.Length);
        Assert.Equal(4, (tag[8] << 8) | tag[9]);
        // 从生成预设里抽出 TRC tag 单独解析：往返出同一条 sRGB 折线（亮部幂、暗部线性）。
        var curves = IccCurveReader.TryRead(ProfileWithParaTag(tag));
        Assert.NotNull(curves);
        Assert.False(curves!.Unsupported);
        Assert.Equal(Srgb(128 / 255d), curves.Channels[0][128], 6);
        Assert.Equal(10 / 255d / 12.92, curves.Channels[0][10], 6);
    }

    private static double Srgb(double x)
    {
        // 期望值用 s15Fixed16 量化后的参数计算——读回的参数是 1/65536 步长的定点数，
        // 与无限精度的 sRGB 公式差在 1e-6 量级，断言精度 1e-6 时必须对齐口径。
        double Q(double v) => Math.Round(v * 65536) / 65536;
        return x >= Q(0.04045)
            ? Math.Pow(Q(1 / 1.055) * x + Q(0.055 / 1.055), Q(2.4))
            : x * Q(1 / 12.92);
    }

    private static (byte[] Tag, int Size) ExtractTag(byte[] profile, string signature)
    {
        var count = (int)ReadUInt32(profile, 128);
        for (var t = 0; t < count; t++)
        {
            var record = 132 + t * 12;
            if (System.Text.Encoding.ASCII.GetString(profile, record, 4) != signature) continue;
            var offset = (int)ReadUInt32(profile, record + 4);
            var size = (int)ReadUInt32(profile, record + 8);
            return (profile[offset..(offset + size)], size);
        }
        throw new InvalidOperationException("profile 里没有 " + signature + " tag");
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static byte[] ProfileWithParaTag(byte[] tagData)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(new byte[128]);            // 头部：TryRead 只关心 128 偏移开始的 tag 表
        bw.Write(Be32(1));                  // tag 数
        bw.Write("rTRC"u8);
        bw.Write(Be32(144));                // tag 数据偏移 = 128 + 4 + 12
        bw.Write(Be32(tagData.Length));
        bw.Write(tagData);
        return ms.ToArray();
    }

    private static byte[] Be32(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] ParaTag(int type, params double[] parameters)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("para"u8);
        bw.Write(new byte[4]);              // reserved
        bw.Write(new byte[] { (byte)(type >> 8), (byte)type });
        bw.Write(new byte[2]);              // reserved
        foreach (var parameter in parameters)
        {
            var raw = (int)Math.Round(parameter * 65536.0);
            bw.Write(new byte[] { (byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw });
        }
        return ms.ToArray();
    }
}
