using System.Text.Json;
using Microsoft.Win32;
using DeltaForceTune.Wpf.Core;
using DeltaForceTune.Wpf.Services;
using Xunit;

namespace DeltaForceTune.Wpf.Tests;

/// <summary>
/// 纯逻辑单元测试：不触碰注册表与系统设置，只验证核心决策代码。
/// </summary>
public class CoreLogicTests
{
    // ---------- UpdateService.IsNewer ----------

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("0.9", "1.0.0", false)]
    public void IsNewer_compares_versions(string latest, string current, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewer(latest, current));

    [Fact]
    public void IsNewer_returns_false_on_unparseable_input()
    {
        Assert.False(UpdateService.IsNewer("v1.2.3", "1.0.0"));
        Assert.False(UpdateService.IsNewer("", "1.0.0"));
        Assert.False(UpdateService.IsNewer("abc", "1.0.0"));
    }

    // ---------- ItemCatalog 完整性 ----------

    [Fact]
    public void ItemCatalog_has_22_unique_items()
    {
        var ids = ItemCatalog.All.Select(x => x.Id).ToList();
        Assert.Equal(22, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ---------- 预设展开（OptimizationEngine.GetPresetIds）----------

    [Fact]
    public void Full_preset_contains_every_item()
        => Assert.Equal(ItemCatalog.All.Count, OptimizationEngine.GetPresetIds("full").Count);

    [Fact]
    public void Balanced_preset_excludes_the_four_documented_items()
    {
        var ids = OptimizationEngine.GetPresetIds("balanced");
        Assert.Equal(ItemCatalog.All.Count - 4, ids.Count);
        Assert.DoesNotContain("sysmain-off", ids);
        Assert.DoesNotContain("wsearch-off", ids);
        Assert.DoesNotContain("hibernate-off", ids);
        Assert.DoesNotContain("power-tuning", ids);
    }

    [Fact]
    public void SafeOnly_preset_is_the_documented_five_items()
        => Assert.Equal(
            new[] { "dvr-off", "fso-off", "game-mode", "gpu-pref", "transparency-off" },
            OptimizationEngine.GetPresetIds("safe-only").OrderBy(x => x).ToArray());

    [Fact]
    public void Unknown_preset_falls_back_to_balanced()
        => Assert.Equal(
            OptimizationEngine.GetPresetIds("balanced").Count,
            OptimizationEngine.GetPresetIds("not-a-preset").Count);

    /// <summary>预设里的每个 id 都必须真实存在于目录，避免出现“永远失败的选择项”。</summary>
    [Fact]
    public void Every_preset_id_exists_in_catalog()
    {
        foreach (var preset in new[] { "full", "balanced", "safe-only" })
            foreach (var id in OptimizationEngine.GetPresetIds(preset))
                Assert.NotNull(ItemCatalog.All.FirstOrDefault(x => x.Id == id));
    }

    // ---------- BackupService.ConvertValue（JSON -> 注册表值转换）----------

    private static JsonElement JsonOf(string raw)
        => JsonSerializer.Deserialize<JsonElement>(raw);

    [Fact]
    public void ConvertValue_dword_from_json_number()
    {
        var result = BackupService.ConvertValue(JsonOf("8"), RegistryValueKind.DWord);
        Assert.Equal(8, result);
    }

    [Fact]
    public void ConvertValue_dword_wraps_uint32_max()
    {
        // NetworkThrottlingIndex = 0xFFFFFFFF 在 JSON 里是无符号大数，注册表 DWord 需要回绕为 -1。
        var result = BackupService.ConvertValue(JsonOf("4294967295"), RegistryValueKind.DWord);
        Assert.IsType<int>(result);
        Assert.Equal(unchecked((int)4294967295L), (int)result);
    }

    [Fact]
    public void ConvertValue_qword_keeps_long()
    {
        var result = BackupService.ConvertValue(JsonOf("5000000000"), RegistryValueKind.QWord);
        Assert.Equal(5000000000L, result);
    }

    [Fact]
    public void ConvertValue_string_kind_returns_text()
    {
        var result = BackupService.ConvertValue(JsonOf("\"~ HIGH\""), RegistryValueKind.String);
        Assert.Equal("~ HIGH", result);
    }

    [Fact]
    public void ConvertValue_plain_object_passthrough_for_dword()
    {
        var result = BackupService.ConvertValue(1, RegistryValueKind.DWord);
        Assert.Equal(1, Convert.ToInt32(result));
    }

    // ---------- PowerShellRunner.Quote（参数安全引用）----------

    [Fact]
    public void Quote_leaves_safe_tokens_bare()
        => Assert.Equal("abc-123_x.y", PowerShellRunner.Quote("abc-123_x.y"));

    [Fact]
    public void Quote_wraps_values_with_spaces()
        => Assert.Equal("'C:/Games/Delta Force/game.exe'", PowerShellRunner.Quote("C:/Games/Delta Force/game.exe"));

    [Fact]
    public void Quote_escapes_embedded_single_quotes()
        => Assert.Equal("'it''s'", PowerShellRunner.Quote("it's"));

    [Fact]
    public void Quote_handles_empty_string()
        => Assert.Equal("''", PowerShellRunner.Quote(""));
}
