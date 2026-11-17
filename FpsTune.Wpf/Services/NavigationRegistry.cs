using System.Windows.Controls;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 页面注册表：路由键 → 页面构造。**新增页面只改这一处**（MainWindow 的页面工厂、导航跳转都由它推导）。
/// 侧栏按钮名不放在这里——WPF 的 x:Name 字段是 MainWindow 实例成员，跨类引用只能靠反射，不值得；
/// 侧栏映射见 MainWindow 的 <c>_navButtons</c>。
/// </summary>
internal sealed record NavPage(string Key, Func<UserControl> Create);

internal static class NavigationRegistry
{
    internal static readonly IReadOnlyList<NavPage> Pages =
    [
        new("home", () => new ConsoleView()),
        new("games", () => new GameLibraryView()),
        new("quick", () => new QuickOptimizeView()),
        new("detect", () => new DetectView()),
        new("opt", () => new OptimizeView()),
        new("display", () => new DisplayQualityView()),
        new("filters", () => new FilterView()),
        new("crosshair", () => new CrosshairView()),
        new("deltafix", () => new DeltaFixView()),
        new("session", () => new SessionView()),
        new("backup", () => new BackupLogView()),
        new("tools", () => new MaintenanceView()),
        new("startup", () => new StartupAuditView()),
        new("settings", () => new SettingsView()),
    ];

    internal static IReadOnlyDictionary<string, Func<UserControl>> Factories { get; } =
        Pages.ToDictionary(p => p.Key, p => p.Create, StringComparer.Ordinal);
}
