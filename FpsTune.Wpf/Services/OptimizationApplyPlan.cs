using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record OptimizationReviewItem(string Id, string Name, string Current, string Description,
    string SideEffect, bool RequiresAdmin, bool RequiresReboot)
{
    public string Requirements => string.Join(" · ", new[] {
        RequiresAdmin ? Str.T("Str.ReviewNeedsAdmin") : null,
        RequiresReboot ? Str.T("Str.ReviewNeedsRestart") : null
    }.OfType<string>());
}

public sealed class OptimizationApplyPlan
{
    public IReadOnlyList<OptimizationReviewItem> Items { get; }
    public IReadOnlyList<string> Ids { get; }
    public string? GamePath { get; }
    public int HiddenCount { get; }
    public string GameLabel => string.IsNullOrWhiteSpace(GamePath) ? Str.T("Str.ReviewGlobal") : Core.GamePathService.LabelFor(GamePath);
    public string Summary => Str.T("Str.ReviewSummary", Items.Count, Items.Count(i => i.RequiresAdmin), Items.Count(i => i.RequiresReboot));
    private OptimizationApplyPlan(OptimizationReviewItem[] items, string? gamePath, int hiddenCount)
    {
        Items = Array.AsReadOnly(items); Ids = Array.AsReadOnly(items.Select(i => i.Id).ToArray());
        GamePath = gamePath; HiddenCount = hiddenCount;
    }
    public static OptimizationApplyPlan Capture(IEnumerable<OptimizationItemViewModel> source,
        Func<OptimizationItemViewModel, bool> isVisible, string? gamePath)
    {
        var known = ItemCatalog.All.Where(i => i.Available).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var selected = source.Where(i => i.IsChecked && i.Available && known.ContainsKey(i.Id)).DistinctBy(i => i.Id).ToArray();
        return new(selected.Select(i => new OptimizationReviewItem(i.Id, known[i.Id].DisplayName, i.Current, known[i.Id].DisplayDescription,
            known[i.Id].DisplaySideEffect, i.RequiresAdmin, i.RequiresReboot)).ToArray(), gamePath, selected.Count(i => !isVisible(i)));
    }
}
