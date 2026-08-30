using System.Text.Json;
using Microsoft.Win32;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

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
    public void ItemCatalog_has_33_unique_items()
    {
        var ids = ItemCatalog.All.Select(x => x.Id).ToList();
        Assert.Equal(33, ids.Count);
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

    // ---------- disabledynamictick 状态与备份消费守卫 ----------

    [Theory]
    [InlineData("", "Absent")]
    [InlineData("disabledynamictick    no", "No")]
    [InlineData("disabledynamictick    yes", "Yes")]
    public void DynamicTick_parser_distinguishes_absent_no_and_yes(string output, string expected)
        => Assert.Equal(expected, NativeSystem.ParseDynamicTickState(output).ToString());

    [Fact]
    public void RestoreAll_keeps_a_backup_file_when_any_record_fails()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fpstune-restore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        BackupService.BackupDirOverride = tmp;
        var file = Path.Combine(tmp, "csharp-backup-test.json");
        try
        {
            File.WriteAllText(file, "[{\"Id\":\"bad\",\"Kind\":\"unknown\"}]");

            var result = OptimizationEngine.RestoreAsync().GetAwaiter().GetResult();

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("[失败]", result.Output);
            Assert.True(File.Exists(file), "失败的整份备份不得被消费");
            Assert.False(File.Exists(file + ".restored"));
        }
        finally
        {
            BackupService.BackupDirOverride = null;
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Backup_file_guards_keep_legacy_PowerShell_documents_out_of_CSharp()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fpstune-backup-guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var powershellFile = Path.Combine(tmp, "backup-legacy.json");
            File.WriteAllText(powershellFile, "{\"schema\":\"v1\",\"tool\":\"delta-optimizer\",\"items\":[]}");

            Assert.False(BackupService.IsCSharpBackupFile(powershellFile));
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
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

    // ---------- ExperimentHistory（A/B 历史趋势解析）----------

    [Fact]
    public void ExperimentHistory_parses_jsonl_lines_in_order()
    {
        const string jsonl = """
            {"time":"2026-08-30T10:00:00","kind":"baseline","id":"baseline","name":"基线","summary":{"avgFps":100.5,"p1Low":55.2,"p99Ms":22.1,"stutters":10,"cv":0.01,"stable":true}}
            {"time":"2026-08-30T10:30:00","kind":"test","id":"group-1","name":"调度组","summary":{"avgFps":106.2,"p1Low":58.1},"keep":true,"reverted":false,"reason":"平均帧率提升 5.7%"}
            not-json
            {"time":"2026-08-30T11:00:00","kind":"test","id":"group-2","name":"后台组","summary":{"avgFps":96.4,"p1Low":52.0},"keep":false,"reverted":true,"reason":"无明显收益"}
            """;
        var runs = ExperimentHistory.ParseLines(jsonl.Split('\n'));
        Assert.Equal(3, runs.Count);
        Assert.Equal("baseline", runs[0].Kind);
        Assert.Equal(100.5, runs[0].AvgFps);
        Assert.Equal(55.2, runs[0].P1Low);
        Assert.Equal("G1", runs[1].ShortLabel);
        Assert.True(runs[1].Keep == true);
        Assert.True(runs[2].Keep == false);
        Assert.Equal("基线", runs[0].ShortLabel);
    }

    [Fact]
    public void ExperimentHistory_skips_corrupt_lines_and_bom()
    {
        const string jsonl = "﻿{\"time\":\"2026-08-30T10:00:00\",\"kind\":\"baseline\",\"id\":\"baseline\",\"name\":\"基线\",\"summary\":{\"avgFps\":90,\"p1Low\":50}}";
        var runs = ExperimentHistory.ParseLines([jsonl]);
        Assert.Single(runs);
        Assert.Equal(90, runs[0].AvgFps);
    }

    // ---------- LegacyMigrations（历史自毁缺陷的守卫）----------
    // v1.0.2~v1.1.5 曾把迁移的 from/to 写成同一个目录并递归删除，
    // 每次启动都会清空 %LOCALAPPDATA%\FpsTune（备份/设置/实验数据）。
    // 这两条测试保证同类问题永不回归。

    [Fact]
    public void LegacyMigration_refuses_same_source_and_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "fpstune-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "keep-me");

        LegacyMigrations.TryMoveDir(root, root);

        Assert.True(File.Exists(Path.Combine(root, "settings.json")), "同路径迁移不得删除数据");
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void LegacyMigration_moves_legacy_dir_and_keeps_source_on_conflict()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fpstune-test-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(baseDir, "old");
        var to = Path.Combine(baseDir, "new");
        Directory.CreateDirectory(Path.Combine(from, "backup"));
        File.WriteAllText(Path.Combine(from, "backup", "b.json"), "{}");
        File.WriteAllText(Path.Combine(from, "orphan.txt"), "moved");
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "conflict.txt"), "newer");
        // 来源侧同名文件制造真实冲突：目标已有同名内容时不得覆盖、来源不得删除。
        File.WriteAllText(Path.Combine(from, "conflict.txt"), "older");

        LegacyMigrations.TryMoveDir(from, to);

        Assert.True(File.Exists(Path.Combine(to, "orphan.txt")));
        Assert.True(File.Exists(Path.Combine(to, "backup", "b.json")));
        Assert.True(File.ReadAllText(Path.Combine(to, "conflict.txt")) == "newer", "同名冲突不得覆盖");
        // 来源目录因存在未搬运的冲突文件而保留，绝不能被递归删除。
        Assert.True(Directory.Exists(from));
        Assert.True(File.Exists(Path.Combine(from, "conflict.txt")));
        Directory.Delete(baseDir, recursive: true);
    }
}
