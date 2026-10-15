using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class WorkspaceCommandTests
{
    [Fact]
    public void Index_has_unique_ids_only_known_routes_and_catalog_ids()
    {
        var commands = WorkspaceCommands.Build();
        Assert.Equal(commands.Count, commands.Select(c => c.Id).Distinct().Count());
        Assert.All(commands, c => Assert.Contains(c.Route, new[] { "home", "detect", "opt", "display", "session", "ab", "backup", "tools", "settings" }));
        Assert.Equal(ItemCatalog.All.Count, commands.Count(c => c.Id.StartsWith("item:")));
        Assert.Contains(commands, c => c.Id == "display:vibicc" && c.Target == "vibicc");
    }
    [Fact]
    public void Search_matches_all_terms_and_ranks_exact_catalog_id_first()
    {
        var search = new WorkspaceCommands();
        var item = ItemCatalog.All.First();
        Assert.Equal("item:" + item.Id, search.Search(item.Id.ToUpperInvariant()).First().Id);
        Assert.Contains(search.Search("dlss NVIDIA"), c => c.Id == "display:dlss");
        Assert.Empty(search.Search("term-that-does-not-exist-47291"));
    }
    [Fact]
    public void History_is_bounded_deduplicated_and_rejects_unknown_commands()
    {
        var search = new WorkspaceCommands();
        foreach (var cmd in WorkspaceCommands.Build().Take(12)) search.Remember(cmd.Id);
        search.Remember("page:backup"); search.Remember("page:backup"); search.Remember("shell:format");
        var results = search.Search("");
        Assert.Equal("page:backup", results[0].Id);
        Assert.Equal(results.Count, results.Select(c => c.Id).Distinct().Count());
        Assert.DoesNotContain(results, c => c.Id.StartsWith("shell:"));
        Assert.InRange(results.Count, 9, 14);
    }
    internal static OptimizationItemViewModel Model(string id, bool admin = false, bool restart = false, bool optimized = false)
    {
        var definition = ItemCatalog.All.Single(i => i.Id == id);
        return new(new OptimizationItem(id, definition.DisplayName, definition.DisplayDescription, definition.DisplaySideEffect,
            admin, restart, optimized, "detected original", false, definition.Group));
    }
    [Fact]
    public void Review_captures_hidden_selection_game_and_values_without_sharing_mutable_selection()
    {
        var definitions = ItemCatalog.All.Where(i => i.Available).Take(2).ToArray();
        var a = Model(definitions[0].Id); var b = Model(definitions[1].Id, true, true);
        a.IsChecked = b.IsChecked = true;
        var source = new List<OptimizationItemViewModel> { a, b, b };
        var plan = OptimizationApplyPlan.Capture(source, vm => ReferenceEquals(vm, a), @"C:\Games\first.exe");
        a.IsChecked = b.IsChecked = false; source.Clear();
        Assert.Equal(2, plan.Items.Count); Assert.Equal(1, plan.HiddenCount);
        Assert.Equal(@"C:\Games\first.exe", plan.GamePath);
        Assert.Equal("detected original", plan.Items[1].Current);
        Assert.True(plan.Items[1].RequiresAdmin); Assert.True(plan.Items[1].RequiresReboot);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.Ids)[0] = "modified");
    }
    [Fact]
    public void Filters_respect_actual_requirements_and_unavailable_items_cannot_enter_plan()
    {
        var id = ItemCatalog.All.First(i => i.Available).Id;
        var ordinary = Model(id); var admin = Model(id, true, true, true);
        Assert.True(OptimizeView.MatchesScope(ordinary, "pending"));
        Assert.False(OptimizeView.MatchesScope(admin, "pending"));
        Assert.False(OptimizeView.MatchesScope(admin, "noAdmin"));
        Assert.False(OptimizeView.MatchesScope(admin, "noRestart"));
        var unavailable = Model("mem-compress-off"); unavailable.IsChecked = true;
        Assert.False(unavailable.IsChecked);
        Assert.False(OptimizeView.MatchesScope(unavailable, "pending"));
        Assert.Empty(OptimizationApplyPlan.Capture([unavailable], _ => true, null).Ids);
    }
}
