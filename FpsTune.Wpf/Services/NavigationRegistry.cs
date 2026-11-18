using System.Windows.Controls;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

/// <summary>页面构造及搜索入口。侧栏顺序和分组由 XAML 定义。</summary>
internal sealed record NavPage(string Key, Func<UserControl> Create, string? TitleKey = null, string SearchAlias = "");

internal static class NavigationRegistry
{
    internal static readonly IReadOnlyList<NavPage> Pages =
    [
        new("home", () => new ConsoleView(), "Str.NavHome", "Str.CommandAliasHome"),
        new("games", () => new GameLibraryView(), "Str.NavGames", "Str.LibrarySearch"),
        new("quick", () => new QuickOptimizeView(), "Str.NavQuickOpt", "Str.QuickOptTitle"),
        new("detect", () => new DetectView(), "Str.NavDetect", "Str.CommandAliasDetect"),
        new("opt", () => new OptimizeView(), "Str.NavOpt", "Str.CommandAliasOptimize"),
        new("display", () => new DisplayQualityView(), "Str.NavDisplay", "Str.CommandAliasDisplay"),
        new("filters", () => new FilterView(), "Str.NavFilters", "Str.CommandAliasFilters"),
        new("crosshair", () => new CrosshairView(), "Str.CrosshairTitle", "crosshair overlay reticle"),
        new("deltafix", () => new DeltaFixView()),
        new("session", () => new SessionView(), "Str.NavSession", "Str.CommandAliasSession"),
        new("backup", () => new BackupLogView(), "Str.NavBackup", "Str.CommandAliasBackup"),
        new("tools", () => new MaintenanceView(), "Str.NavTools", "Str.CommandAliasTools"),
        new("startup", () => new StartupAuditView()),
        new("settings", () => new SettingsView(), "Str.NavSettings", "Str.CommandAliasSettings"),
    ];

    internal static IReadOnlyDictionary<string, Func<UserControl>> Factories { get; } =
        Pages.ToDictionary(p => p.Key, p => p.Create, StringComparer.Ordinal);
}
