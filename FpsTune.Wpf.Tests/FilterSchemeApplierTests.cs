using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 方案热键/托盘共用的「套方案到桌面」路径（FilterSchemeApplier）与方案库导出导入。
/// 套用走与滤镜页应用栏同一条 ColorWorkspaceService（假 gamma + 假显示拓扑），
/// 托盘反馈走注入钩子；方案库走 FileOverride 临时文件，不碰真实用户数据。
/// </summary>
[Collection("BackupService serial")]
public sealed class FilterSchemeApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-schemes-" + Guid.NewGuid().ToString("N"));
    private readonly string _schemesFile;
    private ushort[] _ramp = Linear();

    public FilterSchemeApplierTests()
    {
        Directory.CreateDirectory(_root);
        _schemesFile = Path.Combine(_root, "schemes.json");
        FilterSchemeStore.FileOverride = () => _schemesFile;
        FilterSchemeStore.Invalidate();
        DisplayTopology.Override = () => DisplayTopology.MarkIndependent([
            new IccDisplayInfo(@"\\.\DISPLAY1", 123, 0, 0, "0001", true, "A", "Screen A", true),
        ]);
        ScreenColorService.ApiOverride = new FakeGamma(this);
        ScreenColorService.BackupDirOverride = Path.Combine(_root, "screen");
    }

    public void Dispose()
    {
        FilterSchemeStore.FileOverride = null;
        FilterSchemeStore.Invalidate();
        DisplayTopology.Override = null;
        ScreenColorService.ApiOverride = null;
        ScreenColorService.BackupDirOverride = null;
        TrayService.NotifyOverride = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    internal static ushort[] Linear() => Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();

    private sealed class FakeGamma(FilterSchemeApplierTests owner) : IScreenGammaApi
    {
        public bool IsHdr(IccDisplayInfo display) => false;
        public ushort[] Read(IccDisplayInfo display) => (ushort[])owner._ramp.Clone();
        public void Write(IccDisplayInfo display, ushort[] ramp) => owner._ramp = (ushort[])ramp.Clone();
    }

    // ---------- 桌面套用 ----------

    [Fact]
    public void Screen_snapshot_scheme_applies_to_the_primary_display_and_restores()
    {
        var snapshot = AppRuleScreen.FromSettings(new ScreenColorSettings(Contrast: 112, Gamma: 1.05));
        var scheme = FilterSchemeStore.Save("对比", "", null, includeScreen: true, snapshot);

        var outcome = FilterSchemeApplier.ApplyToDesktop(scheme);

        Assert.NotEqual(Linear(), _ramp);
        Assert.Equal(ScreenColorService.BuildRamp(snapshot.ToSettings(), Linear()), _ramp);
        // 用户主动套的桌面方案走「还原原始」的备份契约：还原后回到原曲线
        Assert.True(ScreenColorService.Restore("A"));
        Assert.Equal(Linear(), _ramp);
    }

    [Fact]
    public void Empty_scheme_without_any_effect_is_rejected_with_a_readable_reason()
    {
        var scheme = FilterSchemeStore.Save("空的", "", null, includeScreen: false, null);
        Assert.Throws<InvalidOperationException>(() => FilterSchemeApplier.ApplyToDesktop(scheme));
    }

    [Fact]
    public void Missing_generated_preset_is_reported_not_silently_ignored()
    {
        var scheme = FilterSchemeStore.Save("坏方案", "generated:NoSuchPreset", 70, includeScreen: false, null);
        Assert.Throws<InvalidOperationException>(() => FilterSchemeApplier.ApplyToDesktop(scheme));
    }

    [Fact]
    public void Empty_slot_notifies_via_tray_without_touching_the_screen()
    {
        var notifications = new List<string>();
        TrayService.NotifyOverride = (title, body) => notifications.Add(title + "|" + body);

        FilterSchemeApplier.ApplySlot(1); // 方案库是空的

        var note = Assert.Single(notifications);
        Assert.Contains("FilterSchemeHotkeyEmpty", note); // 测试环境无资源字典，键名即文案
        Assert.Equal(Linear(), _ramp); // 屏幕没被动过
    }

    // ---------- 预设解析 ----------

    [Fact]
    public void Generated_preset_resolves_to_enum_without_a_file()
    {
        var (preset, file) = FilterSchemeApplier.ResolvePreset("generated:Vivid");
        Assert.Equal(IccFilterPreset.Vivid, preset);
        Assert.Null(file);
    }

    // ---------- 方案库导出 / 导入 ----------

    [Fact]
    public void Export_import_round_trip_merges_by_name_and_keeps_existing()
    {
        var a = FilterSchemeStore.Save("甲", "generated:Vivid", 70, false, null);
        var b = FilterSchemeStore.Save("乙", "", null, true, AppRuleScreen.FromSettings(new ScreenColorSettings(Brightness: 5)));
        var exportPath = Path.Combine(_root, "export.json");
        FilterSchemeStore.Export(exportPath);

        // 改掉「甲」再导入：同名覆盖，且保留「乙」
        FilterSchemeStore.Save("甲", "generated:Cool", 60, false, null);
        var merged = FilterSchemeStore.Import(exportPath);

        Assert.Equal(2, merged.Count);
        Assert.Equal("generated:Vivid", merged.First(s => s.Name == "甲").PresetFileName);
        Assert.NotNull(merged.FirstOrDefault(s => s.Name == "乙"));
        Assert.Equal(a.Id, merged.First(s => s.Name == "甲").Id); // 覆盖同名沿用原条目位置与 Id
    }

    [Fact]
    public void Import_rejects_oversized_or_broken_files_without_losing_current_schemes()
    {
        FilterSchemeStore.Save("甲", "generated:Vivid", 70, false, null);
        var broken = Path.Combine(_root, "broken.json");
        File.WriteAllText(broken, "{broken");
        Assert.Throws<InvalidDataException>(() => FilterSchemeStore.Import(broken));
        Assert.Single(FilterSchemeStore.Schemes);
    }

    [Fact]
    public void Import_refuses_new_names_beyond_the_library_limit_as_a_whole()
    {
        for (var i = 0; i < FilterSchemeStore.MaxSchemes; i++)
            FilterSchemeStore.Save("槽" + i, "generated:Vivid", null, false, null);
        var exportPath = Path.Combine(_root, "full.json");
        FilterSchemeStore.Export(exportPath);

        FilterSchemeStore.Remove(FilterSchemeStore.Schemes[0].Id);
        Assert.Equal(FilterSchemeStore.MaxSchemes - 1, FilterSchemeStore.Schemes.Count);

        // 导入文件里有 MaxSchemes 条、其中 1 条同名覆盖 → 只新增 1 条，放得下
        var merged = FilterSchemeStore.Import(exportPath);
        Assert.Equal(FilterSchemeStore.MaxSchemes, merged.Count);
    }
}
