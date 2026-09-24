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
[Collection("BackupService serial")]
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

    /// <summary>
    /// 剥掉**整行**注释（`//`、`///`、`/*`、`*`），用于「某 API 只应在某处调用」这类源码守卫——
    /// 否则文档注释里提到 API 名就会被误判成调用（本守卫首次运行时正是这样误报的）。
    /// 局限：不处理与代码同行的块注释；本仓库没有这种写法。
    /// </summary>
    private static string StripCommentLines(string source)
        => string.Join("\n", source
            .Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                           && !line.StartsWith("*", StringComparison.Ordinal)
                           && !line.StartsWith("/*", StringComparison.Ordinal)));

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
    public void Catalog_has_33_unique_items()
    {
        var (ids, _) = LoadCatalog();
        Assert.Equal(33, ids.Count);
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
    public void Every_item_has_a_known_group()
    {
        var known = new HashSet<string> { "键鼠", "图形显示", "网络", "电源", "系统与调度" };
        var json = File.ReadAllText(RepoFile("catalog", "catalog.json"));
        using var doc = JsonDocument.Parse(json);
        foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var g = it.GetProperty("group").GetString() ?? "";
            Assert.Contains(g, known);
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
            "gpu-pstate-lock", "dyntick-off",
            "keyboard-latency", "usb-power-save-off", "net-nagle-off"
        };
        var (_, reboot) = LoadCatalog();
        var actual = reboot.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
        Assert.Equal(expected, actual);
    }

    // ---------- 预设解析（P2：未知预设回退只允许一次，坏 catalog 必须明确报错） ----------

    private const string MinimalCatalogJson = """
        {"items":[{"id":"a"},{"id":"b"},{"id":"c"}],
         "presets":{"balanced":{"exclude":["c"]},"safe-only":{"include":["a","b"]}}}
        """;

    [Fact]
    public void ResolvePreset_unknown_name_falls_back_to_balanced()
    {
        var ids = OptimizationCatalog.ResolvePresetFromJson(MinimalCatalogJson, "no-such-preset");
        Assert.Equal(new[] { "a", "b" }, ids);
    }

    [Fact]
    public void ResolvePreset_is_case_insensitive_for_known_names()
    {
        var ids = OptimizationCatalog.ResolvePresetFromJson(MinimalCatalogJson, "SAFE-Only");
        Assert.Equal(new[] { "a", "b" }, ids);
    }

    [Fact]
    public void Catalog_without_balanced_throws_clearly_instead_of_stack_overflow()
    {
        // 修复前：未知/缺失 balanced 会无限递归 ResolvePreset("balanced") → StackOverflow 崩进程
        var json = """{"items":[{"id":"a"}],"presets":{"safe-only":{"include":["a"]}}}""";
        var ex = Assert.Throws<InvalidOperationException>(
            () => OptimizationCatalog.ResolvePresetFromJson(json, "safe-only"));
        Assert.Contains("缺少必需预设", ex.Message);
        Assert.Contains("balanced", ex.Message);
    }

    [Fact]
    public void Malformed_balanced_fails_explicitly_after_single_fallback()
    {
        // balanced 存在但既无 include 也无 exclude：回退一次后必须明确报错，不允许再递归
        var json = """{"items":[{"id":"a"}],"presets":{"balanced":{},"safe-only":{"include":["a"]}}}""";
        var ex = Assert.Throws<InvalidOperationException>(
            () => OptimizationCatalog.ResolvePresetFromJson(json, "whatever"));
        Assert.Contains("无法解析", ex.Message);
    }

    [Fact]
    public void Real_catalog_presets_resolve_to_nonempty_known_ids()
    {
        // 视图（OptimizeView）与 CLI/控制台页已统一走 ResolvePreset；
        // 此测试守卫 catalog 里每个预设都能解析出非空且真实存在的 id 集合。
        var order = OptimizationCatalog.ItemOrder;
        Assert.Contains("balanced", OptimizationCatalog.PresetNames);
        Assert.Contains("safe-only", OptimizationCatalog.PresetNames);
        foreach (var name in OptimizationCatalog.PresetNames)
        {
            var ids = OptimizationCatalog.ResolvePreset(name);
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.Contains(id, order));
        }
    }

    // ---------- 版本号单源化 ----------

    [Fact]
    public void Assembly_version_matches_Directory_Build_props()
    {
        // 版本唯一来源拆为 VersionPrefix（数字三段，装配版本/发布/安装器共用）
        // 与 VersionSuffix（预发布标识，如 beta，只进 InformationalVersion 展示）
        var propsText = File.ReadAllText(
            RepoFile("Directory.Build.props"), Encoding.UTF8);
        var m = Regex.Match(propsText, "<VersionPrefix>([^<]+)</VersionPrefix>");
        Assert.True(m.Success, "Directory.Build.props 缺少 <VersionPrefix>");
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.Groups[1].Value.Trim());

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
            Assert.Matches(@"^csharp-backup-\d{8}-\d{6}-\d{3}-[0-9a-f]{8}\.json$", Path.GetFileName(file));

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

    // ---------- catalog 文案国际化（P2-1，见 docs/dev/PLAN-P2-1-catalog-i18n.md） ----------

    [Fact]
    public void Every_catalog_item_has_complete_english_text_free_of_chinese()
    {
        // 纯数据守卫：直接读 catalog JSON、不经 LangService，因此与当前界面语言无关，
        // 不会与并行测试类里改语言的用例竞态。
        var json = File.ReadAllText(RepoFile("catalog", "catalog.json"));
        using var doc = JsonDocument.Parse(json);
        var cjk = new Regex(@"[\u4e00-\u9fff\u3040-\u30ff]");
        var problems = new List<string>();

        foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var id = it.GetProperty("id").GetString()!;
            foreach (var (zhKey, enKey) in new[]
                     {
                         ("name", "nameEn"),
                         ("description", "descriptionEn"),
                         ("sideEffect", "sideEffectEn"),
                     })
            {
                if (!it.TryGetProperty(enKey, out var enNode))
                {
                    problems.Add($"{id}: 缺少 {enKey}");
                    continue;
                }

                var zh = (it.GetProperty(zhKey).GetString() ?? "").Trim();
                var en = (enNode.GetString() ?? "").Trim();

                // 中文非空 ⇒ 英文必须非空；中文为空 ⇒ 英文也应留空（不给空白项凭空加戏）
                if (zh.Length > 0 && en.Length == 0)
                    problems.Add($"{id}: {zhKey} 有中文但 {enKey} 为空");
                if (zh.Length == 0 && en.Length > 0)
                    problems.Add($"{id}: {zhKey} 为空但 {enKey} 非空");
                if (cjk.IsMatch(en))
                    problems.Add($"{id}: {enKey} 含中日韩字符 -> {en}");
            }
        }

        Assert.True(problems.Count == 0,
            $"catalog 英文文案不完整（共 {problems.Count} 处）：\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Catalog_item_text_follows_the_ui_language()
    {
        var original = FpsTune.Wpf.Services.LangService.Current;
        try
        {
            var def = OptimizationCatalog.Items.Single(x => x.Id == "mouse-accel-off");

            FpsTune.Wpf.Services.LangService.SetCurrentForTest(FpsTune.Wpf.Services.LangService.EnUs);
            Assert.Equal("Disable mouse acceleration", def.DisplayName);
            Assert.StartsWith("Turns off Windows pointer precision", def.DisplayDescription);

            FpsTune.Wpf.Services.LangService.SetCurrentForTest(FpsTune.Wpf.Services.LangService.ZhCn);
            Assert.Equal("关闭鼠标加速", def.DisplayName);
            Assert.StartsWith("关闭 Windows 指针精度增强", def.DisplayDescription);
        }
        finally
        {
            FpsTune.Wpf.Services.LangService.SetCurrentForTest(original);
        }
    }

    [Fact]
    public void Missing_english_text_falls_back_to_chinese_instead_of_showing_blank()
    {
        var original = FpsTune.Wpf.Services.LangService.Current;
        try
        {
            FpsTune.Wpf.Services.LangService.SetCurrentForTest(FpsTune.Wpf.Services.LangService.EnUs);

            // 完全没有英文：必须回退中文，不能显示空白
            var bare = new OptimizationItemDefinition(
                "x", "中文名", "中文说明", "中文副作用", false, false, false, "registry", "键鼠");
            Assert.Equal("中文名", bare.DisplayName);
            Assert.Equal("中文说明", bare.DisplayDescription);
            Assert.Equal("中文副作用", bare.DisplaySideEffect);

            // 英文是空白串（而非 null）：同样必须回退
            var blank = bare with { NameEn = "   ", DescriptionEn = "", SideEffectEn = " " };
            Assert.Equal("中文名", blank.DisplayName);
            Assert.Equal("中文说明", blank.DisplayDescription);
            Assert.Equal("中文副作用", blank.DisplaySideEffect);
        }
        finally
        {
            FpsTune.Wpf.Services.LangService.SetCurrentForTest(original);
        }
    }

    [Fact]
    public void Cli_startup_path_never_loads_the_ui_language()
    {
        // 架构不变量：CLI 分支必须在 LangService.Load() 之前 return，
        // 否则 `-Detect -Json` 的输出会随用户的语言设置变化，而它是机器协议、必须稳定。
        // 背景见 docs/dev/PLAN-P2-1-catalog-i18n.md。
        var app = StripCommentLines(File.ReadAllText(RepoFile("FpsTune.Wpf", "App.xaml.cs"), Encoding.UTF8));
        var cliDispatch = app.IndexOf("CliHost.Run(e.Args)", StringComparison.Ordinal);
        var loadLang = app.IndexOf("LangService.Load()", StringComparison.Ordinal);

        Assert.True(cliDispatch >= 0, "App.xaml.cs 里找不到 CLI 分发点，请同步更新本守卫");
        Assert.True(loadLang >= 0, "App.xaml.cs 里找不到 LangService.Load()，请同步更新本守卫");
        Assert.True(cliDispatch < loadLang,
            "CLI 分支必须先于 LangService.Load() 返回，否则 -Json 输出会随界面语言变化");

        // 且不得有第二处 Load()——那会绕过上面的顺序保证。剥注释后再匹配，避免误报。
        var root = Path.Combine(RepoRoot(), "FpsTune.Wpf");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => Path.GetFileName(p) != "App.xaml.cs")
            .Where(p => Regex.IsMatch(StripCommentLines(File.ReadAllText(p)), @"LangService\s*\.\s*Load\s*\("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "LangService.Load() 只应在 App.xaml.cs 的 GUI 分支调用，却发现: " + string.Join(", ", offenders));
    }
}
