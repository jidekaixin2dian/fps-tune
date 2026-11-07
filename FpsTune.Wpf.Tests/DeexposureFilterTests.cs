using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 去曝光与滤镜分类专项：Highlight 色调映射的端点保持与方向、去曝光预设生成、
/// 生成式滤镜预设流、43 个内置预设的分类覆盖。
/// </summary>
[Collection("BackupService serial")]
public sealed class DeexposureFilterTests : IDisposable
{
    private readonly string _dir;

    public DeexposureFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        UserDataPaths.RootOverride = _dir;
    }

    public void Dispose() => UserDataPaths.RootOverride = null;

    [Fact]
    public void Highlight_mapping_keeps_endpoints()
    {
        var pressed = new ScreenColorSettings(Highlight: -20);
        pressed.Validate();
        Assert.Equal(0, ScreenColorService.MapTone(0, pressed, 100), 9);
        Assert.Equal(1, ScreenColorService.MapTone(1, pressed, 100), 9);
    }

    [Fact]
    public void Negative_highlight_darkens_highlights_only()
    {
        var neutral = new ScreenColorSettings();
        var pressed = new ScreenColorSettings(Highlight: -20);
        // 高光段被压暗
        Assert.True(ScreenColorService.MapTone(0.9, pressed, 100) < ScreenColorService.MapTone(0.9, neutral, 100));
        // 暗部不受影响（smoothstep 窗从 0.5 开始）
        Assert.Equal(ScreenColorService.MapTone(0.3, neutral, 100), ScreenColorService.MapTone(0.3, pressed, 100), 9);
        // 正值方向相反（白点提亮）
        var lifted = new ScreenColorSettings(Highlight: 20);
        Assert.True(ScreenColorService.MapTone(0.9, lifted, 100) > ScreenColorService.MapTone(0.9, neutral, 100));
    }

    [Fact]
    public void Highlight_mapping_stays_monotonic()
    {
        var pressed = new ScreenColorSettings(Highlight: -25);
        double last = -1;
        for (var i = 0; i <= 256; i++)
        {
            var y = ScreenColorService.MapTone(i / 256d, pressed, 100);
            Assert.True(y >= last - 1e-9, $"non-monotonic at {i / 256d:0.###}");
            last = y;
        }
    }

    [Fact]
    public void Highlight_out_of_range_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenColorSettings(Highlight: -30).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenColorSettings(Highlight: 30).Validate());
    }

    [Fact]
    public void Deexposure_presets_generate_valid_icc()
    {
        foreach (var preset in new[] { IccFilterPreset.Deexposure, IccFilterPreset.DeexposureStrong })
        {
            var bytes = IccProfileGenerator.Build(preset);
            Assert.True(bytes.Length > 132);
            Assert.Equal("acsp", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
        }
    }

    [Fact]
    public void Generated_presets_flow_through_library()
    {
        var presets = GameFilterLibrary.Presets;
        var light = presets.FirstOrDefault(p => p.FileName == "generated:Deexposure");
        var strong = presets.FirstOrDefault(p => p.FileName == "generated:DeexposureStrong");
        Assert.NotNull(light);
        Assert.NotNull(strong);
        Assert.Equal("deexpose", light!.Category);
        Assert.Equal("deexpose", strong!.Category);
        var bytes = GameFilterLibrary.ReadPreset("generated:Deexposure");
        Assert.True(bytes.Length > 132);
        Assert.Equal("acsp", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
    }

    [Fact]
    public void All_builtin_presets_have_category()
    {
        var presets = GameFilterLibrary.Presets.Where(p => p.Builtin && !p.FileName.StartsWith("generated:", StringComparison.Ordinal)).ToList();
        // 内置 ICC 会随版本继续增加；这里锁“至少原有 43 条且全部有分类”，不把新增素材当失败。
        Assert.True(presets.Count >= 43, $"built-in preset count={presets.Count}");
        Assert.All(presets, p => Assert.Contains(p.Category, new[] { "soft", "deexpose", "bright", "color", "game", "face" }));
        // 每个分类都有成员
        Assert.Contains(presets, p => p.Category == "soft");
        Assert.Contains(presets, p => p.Category == "deexpose");
        Assert.Contains(presets, p => p.Category == "bright");
        Assert.Contains(presets, p => p.Category == "color");
        Assert.Contains(presets, p => p.Category == "game");
        Assert.Contains(presets, p => p.Category == "face");
    }

    [Fact]
    public void Icc_choice_list_includes_deexposure()
    {
        var keys = new[] { IccFilterPreset.Deexposure, IccFilterPreset.DeexposureStrong }
            .Select(IccProfileGenerator.ResourceKeysFor).ToList();
        Assert.Equal("Str.IccDeexposure", keys[0].Item1);
        Assert.Equal("Str.IccDeexposureStrong", keys[1].Item1);
    }
}
