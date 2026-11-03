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
            // 结构对齐 1U：预设自带 vcgt 校准表（3×257 u16 = 1554 字节），观感就在这条表里。
            Assert.True(ContainsTag(icc, "vcgt"), $"{preset} 的剖面里没有 vcgt");
            Assert.Equal(1554, VcgtSize(icc));
            Assert.True(curves.EncodedRamp, $"{preset} 的曲线应当来自 vcgt（编码值→编码值）");
            if (!curves.IsIdentity) parsed++;
        }
        Assert.True(parsed >= 8, $"生成式预设里只有 {parsed} 条改变了曲线");
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
}
