using System.Text.RegularExpressions;
using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 后台服务审查清单的静态守卫：清单里的每一条都要有中英两套文案、
/// 引用的优化项必须真的存在于 catalog，读取失败时不能凭猜补齐状态。
/// </summary>
public class ServiceAuditTests
{
    private static readonly Regex KeyDecl = new(@"<s:String\s+x:Key=""(Str\.[A-Za-z0-9_.]+)""");

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

    private static HashSet<string> Keys(string locale)
        => new(KeyDecl.Matches(File.ReadAllText(
                Path.Combine(RepoRoot(), "FpsTune.Wpf", "Resources", $"Strings.{locale}.xaml")))
            .Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static HashSet<string> CatalogItemIds()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "catalog", "catalog.json")));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_definition_has_labels_and_reasons_in_both_locales()
    {
        var zh = Keys("zh-CN");
        var en = Keys("en-US");
        var missing = new List<string>();

        void Check(string key, string owner)
        {
            if (!zh.Contains(key)) missing.Add($"zh-CN 缺 {key}（{owner}）");
            if (!en.Contains(key)) missing.Add($"en-US 缺 {key}（{owner}）");
        }

        foreach (var definition in ServiceAuditCatalog.All)
        {
            Check(definition.LabelKey, definition.Name);
            Check(definition.ReasonKey, definition.Name);
            Check(ServiceAuditCatalog.AdviceLabelKey(definition.Advice), definition.Name);
        }

        foreach (var group in ServiceAuditCatalog.GroupOrder)
            Check(ServiceAuditCatalog.GroupLabelKey(group), group.ToString());
        foreach (var startType in Enum.GetValues<ServiceStartType>())
            Check(ServiceAuditCatalog.StartTypeLabelKey(startType), startType.ToString());

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void Service_names_are_unique_and_match_catalog_links()
    {
        var names = ServiceAuditCatalog.All.Select(d => d.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var catalog = CatalogItemIds();
        foreach (var definition in ServiceAuditCatalog.All.Where(d => d.CatalogItemId is { Length: > 0 }))
            Assert.True(catalog.Contains(definition.CatalogItemId!),
                $"{definition.Name} 指向的优化项 {definition.CatalogItemId} 不在 catalog 里");
    }

    [Fact]
    public void Every_group_is_used_so_no_row_is_unreachable()
    {
        var used = ServiceAuditCatalog.All.Select(d => d.Group).Distinct().ToHashSet();
        var empty = ServiceAuditCatalog.GroupOrder.Where(group => !used.Contains(group)).ToArray();
        Assert.True(empty.Length == 0, "清单里没有任何服务的分组会在界面上凭空出现：" + string.Join(", ", empty));
    }

    [Fact]
    public void Advice_matches_state_and_only_unfinished_catalog_rows_can_be_reviewed()
    {
        var definitions = ServiceAuditCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        // 建议关的服务还开着：不算符合建议；已经禁用的才算。
        var autoRow = new ServiceAuditRow(definitions["SysMain"], ServiceStartType.Auto, Running: true);
        Assert.False(autoRow.Disabled);
        Assert.True(autoRow.MatchesAdvice); // 视情况
        Assert.True(autoRow.CanReview);

        var disabledRow = new ServiceAuditRow(definitions["SysMain"], ServiceStartType.Disabled, Running: false);
        Assert.True(disabledRow.Disabled);
        Assert.False(disabledRow.CanReview);

        // 建议关的（无优化项）：永远不出现「加入审阅」入口。
        var maps = new ServiceAuditRow(definitions["MapsBroker"], ServiceStartType.Auto, Running: false);
        Assert.False(maps.MatchesAdvice);
        Assert.False(maps.CanReview);

        // 建议保留的：被禁用了才算偏离建议。
        var audio = new ServiceAuditRow(definitions["Audiosrv"], ServiceStartType.Disabled, Running: false);
        Assert.False(audio.MatchesAdvice);
        var audioRunning = new ServiceAuditRow(definitions["Audiosrv"], ServiceStartType.Auto, Running: true);
        Assert.True(audioRunning.MatchesAdvice);
    }

    [Fact]
    public void Unreadable_services_stay_in_the_list_instead_of_disappearing()
    {
        var rows = ServiceAuditCatalog.Evaluate(_ => throw new InvalidOperationException("access denied"),
            _ => throw new InvalidOperationException("access denied"));

        Assert.Equal(ServiceAuditCatalog.All.Count, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.False(row.Readable);
            Assert.False(row.Disabled);
            Assert.False(row.CanReview);
        });
    }

    [Fact]
    public void Evaluate_reads_each_service_and_maps_state_to_running()
    {
        var asked = new List<string>();
        var rows = ServiceAuditCatalog.Evaluate(name =>
        {
            asked.Add(name);
            return name == "WSearch" ? ServiceStartType.Disabled : ServiceStartType.Auto;
        }, name => name == "Audiosrv" ? "RUNNING" : "STOPPED");

        Assert.Equal(ServiceAuditCatalog.All.Select(d => d.Name), asked);
        var wsearch = rows.Single(r => r.Definition.Name == "WSearch");
        Assert.True(wsearch.Disabled);
        Assert.False(wsearch.Running);
        var audio = rows.Single(r => r.Definition.Name == "Audiosrv");
        Assert.True(audio.Running);
    }
}
