namespace FpsTune.Wpf.Services;

/// <summary>One result vocabulary for manual cleanup and saved preparation plans.</summary>
internal static class MemoryCleanupSummary
{
    private static string Size(ulong value) => $"{value / (1024d * 1024 * 1024):F2} GiB";
    internal static string Stage(MemoryCleanupProgress progress) => progress.Stage switch
    {
        "discovery" => Str.T("Str.MemoryFindingApps"),
        "apps" => Str.T("Str.MemoryAppsStage", progress.Completed, progress.Total, progress.Name),
        "standby" => Str.T("Str.MemoryStandbyStage"),
        _ => Str.T("Str.MemoryMeasureStage")
    };
    internal static bool Succeeded(MemoryCleanupRun result, int targets) => !result.Canceled && result.Error is null
        && result.ReadError is null && result.After is not null && result.StandbyCompleted
        && result.Apps.Count == targets && result.Apps.All(a => a.Error is null);
    internal static string Format(MemoryCleanupRun result, int targets, bool apps, bool standby, double seconds)
    {
        var partial = result.Apps.Any(a => a.Error is not null) || apps && result.Apps.Count < targets;
        var lines = new List<string> { Str.T(result.Canceled ? "Str.MemoryStopped" : result.Error is not null
            ? "Str.MemoryRunFailed" : partial ? "Str.MemoryRunPartial" : "Str.MemoryRunDone", seconds.ToString("F1")) };
        if (apps) lines.Add(Str.T("Str.MemoryAppsSummary", result.Apps.Count(a => a.Error is null),
            result.Apps.Count(a => a.Error is not null), Math.Max(0, targets - result.Apps.Count)));
        if (apps) lines.AddRange(result.Apps.Where(a => a.Error is not null).Take(8)
            .Select(a => Str.T("Str.MemoryAppFailed", a.Name, PrivacyScrub.Sanitize(a.Error!))));
        if (standby) lines.Add(Str.T(result.StandbyCompleted ? "Str.MemoryStandbyDone" : "Str.MemoryStandbyIncomplete"));
        if (result.Error is not null) lines.Add(PrivacyScrub.Sanitize(result.Error));
        if (result.After is { } after)
        {
            lines.Add(Str.T("Str.MemoryAvailableChange", Size(result.Before.Available), Size(after.Available)));
            var change = (double)after.Available - result.Before.Available;
            lines.Add(Str.T(change >= 0 ? "Str.MemoryAvailableIncreased" : "Str.MemoryAvailableDecreased", Size((ulong)Math.Abs(change))));
            if (result.Before.Standby is { } beforeCache && after.Standby is { } afterCache)
                lines.Add(Str.T("Str.MemoryStandbyChange", $"{beforeCache / (1024d * 1024):F1} MiB", $"{afterCache / (1024d * 1024):F1} MiB"));
        }
        else lines.Add(Str.T("Str.MemoryRunReadFailed", PrivacyScrub.Sanitize(result.ReadError ?? "")));
        return string.Join("\n", lines);
    }
}
