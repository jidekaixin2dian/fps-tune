using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record WorkspaceCommand(string Id, string Title, string Description, string Group,
    string Route, string? Target, string SearchText);

// Navigation only: no shell text, system writes, background polling or network queries.
public sealed class WorkspaceCommands
{
    private readonly List<string> _recent = [];
    public static IReadOnlyList<WorkspaceCommand> Build()
    {
        var entries = new List<WorkspaceCommand>();
        foreach (var (route, title, alias) in new[] {
            ("home", "Str.NavHome", "Str.CommandAliasHome"), ("detect", "Str.NavDetect", "Str.CommandAliasDetect"),
            ("opt", "Str.NavOpt", "Str.CommandAliasOptimize"), ("display", "Str.NavDisplay", "Str.CommandAliasDisplay"),
            ("session", "Str.NavSession", "Str.CommandAliasSession"), ("ab", "Str.NavAb", "Str.CommandAliasExperiment"),
            ("backup", "Str.NavBackup", "Str.CommandAliasBackup"), ("tools", "Str.NavTools", "Str.CommandAliasTools"),
            ("settings", "Str.NavSettings", "Str.CommandAliasSettings") })
            Add("page:" + route, title, "Str.CommandPage", route, null, Str.T(alias));
        foreach (var (target, title, aliases) in new[] {
            ("dlss", "Str.CommandDlss", "DLSS NVIDIA RTX K J M preset"),
            ("vibicc", "Str.CommandColor", "ICC ICM digital vibrance saturation color monitor"),
            ("drs", "Str.CommandDrs", "NVIDIA DRS 3D VSync texture shader cache"),
            ("advice", "Str.CommandAdvice", "AMD Intel NVIDIA driver checklist advice") })
            Add("display:" + target, title, "Str.NavDisplay", "display", target, aliases);
        foreach (var (target, title, alias) in new[] {
            ("0", "Str.SettingsAppearance", "Str.CommandAliasAppearance"), ("1", "Str.SettingsStartup", "Str.CommandAliasStartup"), ("2", "Str.SettingsGame", "Str.CommandAliasGames"),
            ("3", "Str.SettingsData", "Str.CommandAliasData"), ("4", "Str.Wallpaper", "Str.CommandAliasWallpaper"), ("5", "Str.SectionAbout", "Str.CommandAliasUpdate") })
            Add("settings:" + target, title, "Str.NavSettings", "settings", target,
                Str.T(alias));
        foreach (var item in ItemCatalog.All)
            entries.Add(new("item:" + item.Id, item.DisplayName, item.DisplayDescription, Str.T("Str.ItemsTitle"),
                "opt", item.Id, string.Join(" ", item.Id, item.Name, item.NameEn, item.Description, item.DescriptionEn, item.Group)));
        return entries;

        void Add(string id, string title, string group, string route, string? target, string aliases)
        {
            var name = Str.T(title); var category = Str.T(group);
            entries.Add(new(id, name, Str.T("Str.CommandOpenHint", category), category, route, target, name + " " + category + " " + aliases));
        }
    }

    public IReadOnlyList<WorkspaceCommand> Search(string? query)
    {
        var all = Build();
        var terms = (query ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return _recent.Select(id => all.FirstOrDefault(c => c.Id == id)).OfType<WorkspaceCommand>()
                .Concat(all.Where(c => c.Id.StartsWith("page:", StringComparison.Ordinal)))
                .DistinctBy(c => c.Id).Take(14).ToArray();
        return all.Where(c => terms.All(t => Contains(c.Title, t) || Contains(c.SearchText, t)))
            .OrderBy(c => c.Id.Equals("item:" + query!.Trim(), StringComparison.OrdinalIgnoreCase) ? 0
                : c.Title.StartsWith(query!.Trim(), StringComparison.OrdinalIgnoreCase) ? 1
                : terms.All(t => Contains(c.Title, t)) ? 2 : 3)
            .ThenBy(c => c.Id.StartsWith("item:", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(c => c.Target is null ? 1 : 0)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).Take(40).ToArray();
    }
    public void Remember(string id)
    {
        if (!Build().Any(c => c.Id == id)) return;
        _recent.Remove(id); _recent.Insert(0, id);
        if (_recent.Count > 6) _recent.RemoveRange(6, _recent.Count - 6);
    }
    private static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
