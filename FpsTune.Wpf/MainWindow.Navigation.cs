using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

/// <summary>
/// 主窗口的导航宿主：页面按需创建并缓存、侧栏选中态、路由键到页面的映射、以及各页面的入口方法。
/// <para>
/// 边界：页面清单只有一处（<see cref="NavigationRegistry"/>，路由键 → 页面构造）；
/// 侧栏按钮由 XAML 的路由键建立索引。
/// 新增一个页面 = 注册表加一条 + XAML 加一个 <c>NavButton</c>，不再改 switch。
/// 这里不碰窗口装饰、热键、通知卡与托盘——那些分别是外壳、<c>MainWindow.Hotkeys</c>、
/// <c>MainWindow.Notifications</c> 与 <c>MainWindow.Lifetime</c> 的事。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 首页（控制台/经典概览）首批数据就绪信号：App 用它决定 splash 何时放行。
    /// 首帧渲染 ≠ 数据就绪——控制台概览首启会跑后台检测、经典概览要等 WMI 硬件信息，
    /// 首帧时页面仍是占位内容。页面对应工作完成后调用 <see cref="NotifyHomeDataReady"/>。
    /// </summary>
    public event EventHandler? HomeDataReady;

    internal event EventHandler? StartupNavigationRequested;

    internal void NotifyHomeDataReady() => HomeDataReady?.Invoke(this, EventArgs.Empty);

    // 页面按需创建、创建后缓存：启动只构建首页，显著降低冷启动时间与常驻内存。
    private readonly IReadOnlyDictionary<string, Func<UserControl>> _pageFactories = NavigationRegistry.Factories;
    private readonly Dictionary<string, UserControl> _pageCache = new();

    private Dictionary<string, Views.Controls.NavButton>? _navButtons;

    private UserControl GetPage(string key)
    {
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = _pageFactories[key]();
            _pageCache[key] = page;
        }
        return page;
    }

    private Dictionary<string, Views.Controls.NavButton> BuildNavButtons()
        => SidebarButtons(NavigationStack).ToDictionary(button => (string)button.Tag, StringComparer.Ordinal);

    private static IEnumerable<Views.Controls.NavButton> SidebarButtons(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is Views.Controls.NavButton button)
                yield return button;
            else if (child is Panel group)
                foreach (var nested in SidebarButtons(group))
                    yield return nested;
        }
    }

    private IEnumerable<Views.Controls.NavButton> AllNavButtons => (_navButtons ??= BuildNavButtons()).Values;

    public void NavigateTo(string key)
    {
        if (_navButtons is not null && _navButtons.TryGetValue(key, out var button))
        {
            button.IsChecked = true;      // 选中态驱动 Nav_Checked → SwitchPage
            return;
        }
        // 没有侧栏项的页面（掉帧修复）：直接切页，侧栏保持无选中。
        if (_pageFactories.ContainsKey(key))
            SwitchPage(GetPage(key));
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navButtons is null)
            return;
        if (sender is RadioButton { Tag: string key } && _pageFactories.ContainsKey(key))
        {
            // 侧栏导航分布在多个容器中（WPF 单选钮按逻辑父容器分组，跨容器不互斥），
            // 这里手动保证全组唯一选中：修复"点过设置后其他按钮无法熄灭它、再点设置无响应"。
            foreach (var radio in AllNavButtons)
            {
                if (!ReferenceEquals(radio, sender) && radio.IsChecked == true)
                    radio.IsChecked = false;
            }

            SwitchPage(GetPage(key));
            if (key == "opt" && GetPage("opt") is OptimizeView opt)
                opt.ReloadFromState();
        }
    }

    private void SwitchPage(UserControl page)
    {
        if (ReferenceEquals(PageHost.Content, page))
            return;

        // Present cached content immediately; whole-page fades delayed every navigation
        // and repeatedly rasterized CJK text at fractional translated positions.
        PageHost.Content = page;
        var key = _pageCache.FirstOrDefault(kv => ReferenceEquals(kv.Value, page)).Key;
        if (!string.IsNullOrEmpty(key)) RememberRecentPage(key);
        if (key == "home" && page is ConsoleView console) console.RefreshRecentPages();
        // "掉帧修复"没有侧栏项（入口在首页快捷、滤镜页、调校窗与游戏库），
        // 进入时清掉其他页签的残留选中，保证侧栏任一时刻最多一项高亮。
        // 用缓存键判断而不是取页面实例：GetPage 首次会构造页面，切任何页都会顺带把掉帧页建出来。
        var onDeltaFix = string.Equals(key, "deltafix", StringComparison.Ordinal);
        if (onDeltaFix)
        {
            foreach (var radio in AllNavButtons)
            {
                if (radio.IsChecked == true)
                    radio.IsChecked = false;
            }
        }
        if (!ReferenceEquals(page, GetPage("home"))) StartupNavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>首页"继续使用"：记录最近访问页面（首页本身除外），最多保留 6 个。</summary>
    private void RememberRecentPage(string key)
    {
        if (key == "home") return;
        var settings = SettingsService.Current;
        var recent = settings.RecentPages ??= [];
        recent.Remove(key);
        recent.Insert(0, key);
        if (recent.Count > 6) recent.RemoveRange(6, recent.Count - 6);
        try { SettingsService.Save(settings); }
        catch (Exception) { /* 最近访问记录失败不阻断导航。 */ }
    }

    /// <summary>导航分组折叠：组头勾选态控制子项面板显隐。</summary>
    private void GroupHeader_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string key })
            return;
        var items = FindName("NavGroup" + key + "Items") as Panel;
        if (items is null) return;
        items.Visibility = ((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = !MoreMenu.IsOpen;
    }

    public void ShowOptimizePage()
    {
        NavigateTo("opt");
        if (GetPage("opt") is OptimizeView opt)
            opt.ReloadFromState();
    }

    /// <summary>审查类页面挑好优化项后，直接带进优化页的审阅流程（写入仍由用户在审阅窗确认）。</summary>
    internal void ReviewOptimizationItems(IEnumerable<string> ids)
    {
        var selected = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return;
        ShowOptimizePage();
        if (GetPage("opt") is OptimizeView opt)
            opt.SelectForReview(selected);
    }

    internal void ReviewSelection(IEnumerable<string> ids)
    {
        ShowOptimizePage();
        ((OptimizeView)GetPage("opt")).SelectForReview(ids);
    }

    internal Task<bool> RefreshDetectionAsync() => ((DetectView)GetPage("detect")).RunDetectionAsync();

    /// <summary>0.2.0 M2：概览页切换游戏后，同步检测页（未创建则创建）状态到当前游戏。</summary>
    internal void NotifyGameSwitched()
    {
        if (GetPage("detect") is DetectView detect)
            detect.OnGameSwitched();
    }

    /// <summary>0.2.2 M2 收尾：接入游戏上下文服务（忙检数据源 + 自动补检测）。</summary>
    private void HookGameContextService()
    {
        GameContextService.IsBusyProvider = () => _pageCache.TryGetValue("detect", out var page)
            && page is DetectView d && d.DetectionInFlight;
        GameContextService.GameSwitched += () =>
        {
            NotifyGameSwitched();
            if (GameContextService.NeedsDetection)
            {
                GameContextService.ClearNeedsDetection();
                _ = RefreshDetectionAsync();
            }
        };
    }

    internal void OpenDeltaSession()
    {
        NavigateTo("session");
        ((SessionView)GetPage("session")).PrepareDeltaSession();
    }

    internal async Task OpenPreparationSettingsAsync()
    {
        NavigateTo("home");
        await ((ConsoleView)GetPage("home")).ConfigurePreparationAsync();
    }

    internal void OpenRecordingOptions(string? targetPath = null)
    {
        NavigateTo("session");
        var session = (SessionView)GetPage("session");
        session.OpenAutomationOptions(targetPath);
    }

    internal void OpenDeltaFixFromHome() => NavigateTo("deltafix");

    /// <summary>
    /// 控制台概览首次「基础建议」（P3-9）：先到检测页做一次只读体检建立信任与基线，
    /// 成功后再带着建议项进优化页审阅。检测失败不标记引导已见——下次点击仍会先体检。
    /// </summary>
    internal async Task ReviewSelectionWithIntroAsync(IReadOnlyList<string> ids)
    {
        NavigateTo("detect");
        if (GetPage("detect") is DetectView detect)
        {
            var ok = await detect.RunOnboardingDetectionAsync();
            if (!ok)
            {
                DialogService.Warning(Str.T("Str.DetectAndOptimize"), Str.T("Str.DetectIncomplete"));
                return;
            }
        }
        StateStore.MarkOnboardingSeen();
        ReviewSelection(ids.Count == 0 ? DeltaPreparation.PendingItems(AppState.Items, true) : ids);
    }

    public async Task RunOnboardingAsync()
    {
        // 第一步：先去检测页完成一次检测。
        NavigateTo("detect");
        if (GetPage("detect") is DetectView detect)
        {
            var ok = await detect.RunOnboardingDetectionAsync();
            if (!ok)
            {
                DialogService.Warning(Str.T("Str.DetectAndOptimize"), Str.T("Str.DetectIncomplete"));
                return;
            }
        }

        // 第二步：检测完成后引导用户进入优化页。
        ShowOptimizePage();
        DialogService.Info(Str.T("Str.DetectAndOptimize"), Str.T("Str.DetectDoneHint"));
    }

    public void RefreshHomeContacts()
    {
        // Legacy contact fields remain readable; community feedback is now a global entry.
    }
}
