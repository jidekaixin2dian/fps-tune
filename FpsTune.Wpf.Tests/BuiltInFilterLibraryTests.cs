using System.IO;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 内置滤镜库的"实际可用"证据：每条内置 .icc 都必须通过应用本身使用的显示 profile 校验
/// （<see cref="IccProfileFile.IsDisplayProfile"/> → mscms `OpenColorProfile` + `IsColorProfileValid`），
/// 并且带配套的数字振动档；会员专属那 8 条来自解锁后的加密模块，与免费 43 条同等待遇。
/// </summary>
public class BuiltInFilterLibraryTests
{
    private static readonly (string File, int Vibrance)[] MemberPresets =
    [
        ("18·特调V1.icc", 90), ("19·特调V3.icc", 90), ("20·特调V4.icc", 70), ("21·特调V5.icc", 90),
        ("37·深艳.icc", 83), ("38·浓墨.icc", 50), ("39·航天基地.icc", 68), ("40·沉冷.icc", 68),
    ];

    [Fact]
    public void Every_built_in_icc_passes_the_same_display_profile_check_the_app_uses()
    {
        var presets = GameFilterLibrary.Presets
            .Where(p => p.Builtin && !p.FileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.True(presets.Count >= 51, $"内置 .icc 只有 {presets.Count} 条，会员 8 条没进去？");

        var dir = Path.Combine(Path.GetTempPath(), "fpstune-icc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var preset in presets)
            {
                var bytes = GameFilterLibrary.ReadPreset(preset.FileName);
                Assert.True(bytes.Length > 128, $"{preset.FileName} 字节太少");
                var path = Path.Combine(dir, preset.FileName);
                File.WriteAllBytes(path, bytes);
                Assert.True(IccProfileFile.IsDisplayProfile(path), $"{preset.FileName} 未通过显示 profile 校验");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("18·特调V1.icc", 90, "color")]
    [InlineData("19·特调V3.icc", 90, "bright")]
    [InlineData("20·特调V4.icc", 70, "color")]
    [InlineData("21·特调V5.icc", 90, "deexpose")]
    [InlineData("37·深艳.icc", 83, "color")]
    [InlineData("38·浓墨.icc", 50, "deexpose")]
    [InlineData("39·航天基地.icc", 68, "game")]
    [InlineData("40·沉冷.icc", 68, "deexpose")]
    public void Member_presets_are_shipped_with_their_reference_vibrance(string file, int vibrance, string category)
    {
        var preset = GameFilterLibrary.Presets.SingleOrDefault(p => p.FileName == file);
        Assert.NotNull(preset);
        Assert.Equal(vibrance, preset!.Vibrance);
        Assert.Equal(category, preset.Category);
        Assert.False(string.IsNullOrWhiteSpace(preset.Fit));
        Assert.False(string.IsNullOrWhiteSpace(preset.Avoid));
    }

    [Fact]
    public void Free_presets_also_carry_their_reference_vibrance()
    {
        // 抽样核对 43 条免费预设的伴随振动档（后面全都补齐了）。
        var presets = GameFilterLibrary.Presets;
        Assert.Equal(80, presets.Single(p => p.FileName == "13·日常通用.icc").Vibrance);
        Assert.Equal(100, presets.Single(p => p.FileName == "12·偏冷灰.icc").Vibrance);
        Assert.Equal(90, presets.Single(p => p.FileName == "2.1·原色降曝.icc").Vibrance);
        Assert.All(presets.Where(p => p.Builtin && p.FileName.EndsWith(".icc", StringComparison.Ordinal)),
            p => Assert.True(p.Vibrance is >= 0 and <= 100, $"{p.FileName} 缺振动档"));
    }

    [Fact]
    public void Member_tune_presets_are_inside_our_ranges_and_generate_a_profile()
    {
        foreach (var preset in new[]
                 {
                     IccFilterPreset.VipSaturated, IccFilterPreset.VipDeepVivid,
                     IccFilterPreset.VipWarmClear, IccFilterPreset.VipCoolSuppress,
                 })
        {
            ScreenColorSettings.ForPreset(preset).Validate();
            Assert.True(IccProfileGenerator.Build(preset).Length > 128);
            Assert.NotNull(IccProfileGenerator.VibranceFor(preset));
        }
        Assert.Equal(80, IccProfileGenerator.VibranceFor(IccFilterPreset.VipSaturated));
        Assert.Equal(62, IccProfileGenerator.VibranceFor(IccFilterPreset.VipWarmClear));
    }
}
