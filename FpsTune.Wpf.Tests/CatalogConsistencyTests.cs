using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 结构一致性守卫：catalog.json 是 C# 与 PowerShell 的唯一数据源，
/// 这组测试防止任一引擎悄悄漂移（新增项漏实现、预设引用不存在项、版本号失同步）。
/// </summary>
public class CatalogConsistencyTests
{
    private static string RepoRoot()
    {
        // 从测试输出目录向上找到仓库根（以 catalog/catalog.json 为标志）
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "catalog", "catalog.json")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!;
        }
        throw new InvalidOperationException("未定位到仓库根");
    }

    private static string RepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "缺少文件: " + path);
        return path;
    }

    private static (List<string> ids, Dictionary<string, bool> reboot) LoadCatalog()
    {
        var json = File.ReadAllText(RepoFile("catalog", "catalog.json"));
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        var ids = new List<string>();
        var reboot = new Dictionary<string, bool>();
        foreach (var it in items.EnumerateArray())
        {
            var id = it.GetProperty("id").GetString()!;
            Assert.DoesNotContain(id, ids);
            ids.Add(id);
            reboot[id] = it.GetProperty("reboot").GetBoolean();
        }
        return (ids, reboot);
    }

    [Fact]
    public void Catalog_has_23_unique_items()
    {
        var (ids, _) = LoadCatalog();
        Assert.Equal(23, ids.Count);
    }

    [Fact]
    public void Every_catalog_item_has_a_native_engine_case()
    {
        var (ids, _) = LoadCatalog();
        var engineSrc = File.ReadAllText(
            RepoFile("FpsTune.Wpf", "Core", "NativeOptimizationEngine.cs"));
        var cases = Regex.Matches(engineSrc, @"case\s+""([a-z0-9-]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        var missing = ids.Where(id => !cases.Contains(id)).ToList();
        Assert.True(missing.Count == 0,
            "以下优化项在 NativeOptimizationEngine 缺少 case 分支: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_catalog_item_exists_in_powershell_engine()
    {
        var (ids, _) = LoadCatalog();
        var ps = File.ReadAllText(RepoFile("fps-tune.ps1"));
        var missing = ids.Where(id => !ps.Contains($"id = '{id}'", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "以下优化项在 fps-tune.ps1 缺少实现条目: " + string.Join(", ", missing));
    }

    [Fact]
    public void Preset_references_resolve_to_known_ids()
    {
        var json = File.ReadAllText(RepoFile("catalog", "catalog.json"));
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToHashSet();

        foreach (var preset in doc.RootElement.GetProperty("presets").EnumerateObject())
        {
            foreach (var field in new[] { "include", "exclude" })
            {
                if (!preset.Value.TryGetProperty(field, out var arr))
                    continue;
                foreach (var entry in arr.EnumerateArray())
                {
                    var id = entry.GetString() ?? "";
                    Assert.Contains(id, ids); // 预设不能引用不存在的优化项
                }
            }
        }
    }

    [Fact]
    public void Reboot_items_match_conservative_snapshot()
    {
        // gpu-pstate-lock 曾在 CLI 漏标需重启；此快照锁定保守口径
        var expected = new HashSet<string>
        {
            "power-tuning", "hags", "mpo-off", "sysmain-off", "wsearch-off",
            "hibernate-off", "paging-exec", "mem-compress-off",
            "gpu-pstate-lock", "dyntick-off"
        };
        var (_, reboot) = LoadCatalog();
        var actual = reboot.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
        Assert.Equal(expected, actual);
    }

    // ---------- 版本号单源化 ----------

    [Fact]
    public void Assembly_version_matches_Directory_Build_props()
    {
        var propsText = File.ReadAllText(
            RepoFile("Directory.Build.props"), Encoding.UTF8);
        var m = Regex.Match(propsText, "<Version>([^<]+)</Version>");
        Assert.True(m.Success, "Directory.Build.props 缺少 <Version>");

        var asmVersion = typeof(OptimizationCatalog).Assembly
            .GetName().Version?.ToString(3);

        Assert.Equal(m.Groups[1].Value.Trim(), asmVersion);
    }

    [Fact]
    public void Installer_no_longer_hardcodes_version()
    {
        var iss = File.ReadAllText(RepoFile("installer", "setup.iss"), Encoding.UTF8);
        Assert.DoesNotMatch(@"#define MyAppVersion ""\d", iss);
        Assert.Contains("#error", iss); // 未注入版本时必须构建失败而不是回退旧值
    }

    // ---------- 备份记录序列化往返 ----------

    [Fact]
    public void BackupRecord_json_roundtrip_preserves_fields()
    {
        var record = new BackupRecord
        {
            Id = "hags",
            Kind = "registry",
            Hive = "HKLM",
            Path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            Name = "HwSchMode",
            OldValue = 2,
            ValueKind = "DWord",
            Existed = true,
        };
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var text = JsonSerializer.Serialize(new List<BackupRecord> { record }, opts);

        using var doc = JsonDocument.Parse(text);
        var back = doc.RootElement.EnumerateArray().First();
        Assert.Equal("hags", back.GetProperty("Id").GetString());
        Assert.Equal(2, back.GetProperty("OldValue").GetInt32());
        Assert.True(back.GetProperty("Existed").GetBoolean());

        var restored = JsonSerializer.Deserialize<List<BackupRecord>>(text, opts)!;
        var r = Assert.Single(restored);
        Assert.Equal(record.Id, r.Id);
        Assert.Equal(record.Path, r.Path);
        var je = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(r.OldValue)).RootElement;
        Assert.Equal(2, je.GetInt32());
        Assert.True(r.Existed);
    }

    [Fact]
    public void Capture_writes_readable_backup_file_for_registry_items()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fpstune-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        BackupService.BackupDirOverride = Path.Combine(tmp, "backup");
        try
        {
            // 仅 HKCU 只读采样，不写系统设置；文件落在临时目录。
            var file = BackupService.Capture(new[] { "game-mode", "dvr-off" }, null);
            Assert.True(File.Exists(file));

            var list = JsonSerializer.Deserialize<List<BackupRecord>>(
                File.ReadAllText(file), new JsonSerializerOptions { WriteIndented = true });
            Assert.NotNull(list);
            Assert.NotEmpty(list!);
            Assert.All(list!, r => Assert.True(r.Id is "game-mode" or "dvr-off"));

            Assert.Contains(file, BackupService.ListBackups());
        }
        finally
        {
            BackupService.BackupDirOverride = null;
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }
}
