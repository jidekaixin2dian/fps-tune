using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private UpdateCard? _updateCard;
    private GuideCard? _guideCard;
    private Window? _productDialog;
    private UpdateInfo? _pendingUpdate;
    internal CommunityPreferences CommunityPreferenceStore { get; set; } = new();
    internal void ShowUpdateCard(UpdateInfo info)
    {
        if (_productDialog is not null) { _pendingUpdate = info; return; }
        ShowProductDialog(new UpdateAvailableWindow(info));
    }
    internal void ShowCommunityPrompt(bool automatic = false)
    {
        if (_productDialog is not null || (automatic && CommunityPreferenceStore.IsSuppressed)) return;
        ShowProductDialog(new CommunityWindow(CommunityPreferenceStore.Suppress, ProductLinks.Open));
    }
    private void ShowProductDialog(Window dialog)
    {
        _productDialog = dialog;
        dialog.Owner = this;
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        try { dialog.ShowDialog(); }
        finally { _productDialog = null; }
        if (_pendingUpdate is { } pending)
        {
            _pendingUpdate = null;
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) ShowUpdateCard(pending); }));
        }
    }
    internal void ShowUpdateRecovery(UpdateAttempt attempt)
    {
        if (_updateCard is not null)
        {
            if (_updateCard.IsRunning) { _updateCard.Notification.SetExpanded(true); return; }
            _updateCard.Cancel();
            NotificationStack.Children.Remove(_updateCard);
            _updateCard = null;
        }
        AttachUpdateCard(new UpdateCard(attempt));
    }
    private void AttachUpdateCard(UpdateCard updateCard)
    {
        _updateCard = updateCard;
        var card = _updateCard;
        card.Notification.Expanded += (_, _) => _guideCard?.Notification.SetExpanded(false);
        card.Notification.CloseRequested += (_, _) =>
        {
            card.Cancel();
            NotificationStack.Children.Remove(card);
            _updateCard = null;
            RefreshNotificationArea();
        };
        _guideCard?.Notification.SetExpanded(false);
        NotificationStack.Children.Add(card);
        RefreshNotificationArea();
    }

    private void RefreshNotificationArea() => NotificationArea.Visibility = NotificationStack.Children.Count == 0
        ? Visibility.Collapsed : Visibility.Visible;

    internal void ShowGuideCard()
    {
        if (_guideCard is null)
        {
            _guideCard = new GuideCard(NavigateTo) { Margin = new Thickness(0, 0, 0, 8) };
            var card = _guideCard;
            card.Notification.Expanded += (_, _) => _updateCard?.Notification.SetExpanded(false);
            card.Notification.CloseRequested += (_, _) =>
            {
                NotificationStack.Children.Remove(card);
                _guideCard = null;
                RefreshNotificationArea();
            };
            NotificationStack.Children.Insert(0, card);
        }
        _guideCard.ShowTutorial();
        RefreshNotificationArea();
    }
    private async Task CheckStartupUpdateAsync()
    {
        try
        {
            await Task.Delay(2500, _updateLifetime.Token);
            while (Application.Current.Windows.OfType<SplashWindow>().Any(w => w.IsVisible))
                await Task.Delay(200, _updateLifetime.Token);
            UpdateAttempt? recovery = null;
            try { recovery = await Task.Run(() => UpdateRecovery.ResolveStartup(UpdateService.DisplayVersion, Environment.ProcessPath!), _updateLifetime.Token); }
            catch { /* Recovery metadata must never block app startup. */ }
            if (recovery is not null && recovery.Status != "succeeded") { ShowUpdateRecovery(recovery); return; }
            if (SettingsService.Current.CheckUpdatesOnStartup)
            {
                var info = await UpdateService.CheckAsync(_updateLifetime.Token);
                if (_updateLifetime.IsCancellationRequested) return;
                if (info is not null && UpdateService.IsNewer(info.ReleaseTag ?? info.Version, UpdateService.DisplayVersion))
                {
                    if (IsVisible && WindowState != WindowState.Minimized) ShowUpdateCard(info);
                    return; // One automatic prompt per launch; the community reminder can wait.
                }
            }
            if (!_updateLifetime.IsCancellationRequested && IsVisible && WindowState != WindowState.Minimized
                && !App.SessionService.IsRunning && !ExperimentRunner.IsRunning)
                ShowCommunityPrompt(automatic: true);
        }
        catch (OperationCanceledException) { }
    }
    // 页面按需创建、创建后缓存：启动只构建首页，显著降低冷启动时间与常驻内存。
    private readonly Dictionary<string, Func<UserControl>> _pageFactories;
    private readonly Dictionary<string, UserControl> _pageCache = new();

    // 全局热键 Ctrl+Alt+F
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x1F01;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
    private const uint VK_F = 0x46;

    // 使用非分层窗口和 WindowChrome，保留 DWM 原生窗口动画与圆角。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    /// <summary>
    /// 首页（控制台/经典概览）首批数据就绪信号：App 用它决定 splash 何时放行。
    /// 首帧渲染 ≠ 数据就绪——控制台概览首启会跑后台检测、经典概览要等 WMI 硬件信息，
    /// 首帧时页面仍是占位内容。页面对应工作完成后调用 <see cref="NotifyHomeDataReady"/>。
    /// </summary>
    public event EventHandler? HomeDataReady;
    internal event EventHandler? StartupNavigationRequested;

    internal void NotifyHomeDataReady() => HomeDataReady?.Invoke(this, EventArgs.Empty);

    public MainWindow()
    {
        InitializeComponent();
        InitializeCommandPalette();
        HookGameContextService();

        _pageFactories = new Dictionary<string, Func<UserControl>>
        {
            ["home"] = () => new ConsoleView(),
            ["games"] = () => new GameLibraryView(),
            ["detect"] = () => new DetectView(),
            ["opt"] = () => new OptimizeView(),
            ["display"] = () => new DisplayQualityView(),
            ["filters"] = () => new FilterView(),
            ["quick"] = () => new QuickOptimizeView(),
            ["session"] = () => new SessionView(),
            ["ab"] = () => new AbExperimentView(),
            ["backup"] = () => new BackupLogView(),
            ["tools"] = () => new MaintenanceView(),
            ["settings"] = () => new SettingsView(),
        };

        PageHost.Content = GetPage("home");
        // 托盘图标随应用启动常驻(与最小化设置无关); 设置只控制最小化行为
        if (UserDataPaths.RootOverride is null) TrayService.EnsureCreated();

        // 版本号唯一来源：程序集（编译自 Directory.Build.props）
        var ver = "v" + UpdateService.DisplayVersion;
        TitleVersionText.Text = ver;
        AdminStatusText.Text = AdminHelper.IsAdministrator() ? Str.T("Str.AdminMode") : Str.T("Str.NormalUser");
        UpdateOverviewModeLabel();

        var theme = SettingsService.Current.ThemeMode;
        if (string.IsNullOrWhiteSpace(theme))
            theme = "dark";
        ThemeManager.SetMode(theme);
        RefreshWallpaper();

        StateChanged += OnStateChanged;
        UpdateCaptionState();
        ChromeGrid.SizeChanged += (_, _) => UpdateRootClip();
        Closing += OnMainClosing;
        Closed += (_, _) =>
        {
            App.AutomaticRecording?.Dispose();
            App.CloseHud();
            _updateLifetime.Cancel();
            _updateCard?.Cancel();
            TrayService.Dispose();
            if (_hwndSource is not null)
                UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
        };
        Loaded += (_, _) => { if (UserDataPaths.RootOverride is null) _ = CheckStartupUpdateAsync(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);

        var pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwndSource!.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        if (UserDataPaths.RootOverride is null) ApplyHotkeyRegistration();
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint NativeGetDpiForWindow(nint hwnd);

    private HwndSource? _hwndSource;
    private bool _savingSessionOnExit;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0112 /* WM_SYSCOMMAND */ && ((long)wParam & 0xfff0) == 0xf060 /* SC_CLOSE */)
        {
            handled = true;
            _ = Dispatcher.BeginInvoke(RequestUserClose);
            return nint.Zero;
        }
        if (msg == WM_HOTKEY && wParam == HOTKEY_ID)
        {
            RestoreFromTray();
            handled = true;
        }
        return nint.Zero;
    }

    /// <summary>按设置注册/注销全局热键（设置页开关时调用）。</summary>
    public void ApplyHotkeyRegistration()
    {
        if (_hwndSource is null)
            return;
        UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
        if (SettingsService.Current.HotkeyEnabled)
            RegisterHotKey(_hwndSource.Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_F);
    }

    // ---------- 托盘常驻 ----------

    private async void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateRootClip();
        UpdateCaptionState();
        if (WindowState == WindowState.Minimized && SettingsService.Current.MinimizeToTray)
        {
            // 先让最小化动画落定再决定去向: 托盘图标挂载成功才隐藏窗口,
            // 否则保持任务栏最小化(避免窗口隐藏后托盘也没有、失去入口)。
            // ApplicationIdle 不等于 DWM 过渡完成，给系统最小化动画留出时间。
            await Task.Delay(250);
            if (!IsLoaded || WindowState != WindowState.Minimized)
                return;
            if (TrayService.EnsureCreated())
            {
                Hide();
                TrayService.ShowMinimizedHint();
            }
        }
    }

    /// <summary>从托盘/热键恢复主窗口。</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Activate();
        Focus();
    }

    // ---------- 圆角裁剪 ----------

    /// <summary>
    /// 把窗口内容裁剪到自绘圆角内：标题栏/侧栏的不透明背景否则会溢出到
    /// 圆角外的方角区域，盖掉边框线并形成白色方角。
    /// </summary>
    private void UpdateRootClip()
    {
        var radius = WindowState == WindowState.Maximized ? 0 : 8;
        ChromeGrid.Clip = new RectangleGeometry(
            new Rect(0, 0, ChromeGrid.ActualWidth, ChromeGrid.ActualHeight), radius, radius);
    }

    private UserControl GetPage(string key)
    {
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = _pageFactories[key]();
            _pageCache[key] = page;
        }
        return page;
    }

    public void ShowOptimizePage()
    {
        NavigateTo("opt");
        if (GetPage("opt") is OptimizeView opt)
            opt.ReloadFromState();
    }

    /// <summary>侧栏全部导航单选钮（含分组子项）；互斥与双模式样式切换共用。</summary>
    private Views.Controls.NavButton[] AllNavButtons => new[]
    {
        NavHome, NavGames, NavQuick, NavDetect, NavDisplay, NavFilters,
        NavOpt, NavTools, NavBackup, NavSession, NavAb, NavSettings
    };

    /// <summary>侧栏顶级项（不缩进）；分组子项用 SubItem 样式。</summary>
    private Views.Controls.NavButton[] TopNavButtons => new[] { NavHome, NavFilters, NavSession, NavAb, NavSettings };

    private void UpdateOverviewModeLabel()
    {
        var studio = SettingsService.Current.OverviewMode != "console";
        OverviewModeButton.Header = Str.T(studio ? "Str.CompactConsole" : "Str.Workspace");
        NavigationColumn.Width = new GridLength(studio ? 180 : 0);
        NavigationRow.Height = new GridLength(studio ? 0 : 50);
        Grid.SetRowSpan(TabBar, studio ? 2 : 1);
        Grid.SetColumnSpan(TabBar, studio ? 1 : 2);
        Grid.SetRow(PageHost, studio ? 1 : 2);
        Grid.SetRowSpan(PageHost, studio ? 2 : 1);
        NavigationStack.Orientation = studio ? Orientation.Vertical : Orientation.Horizontal;
        NavigationStack.Margin = new Thickness(12, studio ? 0 : 6, 12, 0);
        NavigationHeading.Visibility = NavigationFooter.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
        // studio=侧栏：分组组头可见（可折叠）、子项竖排、缩进样式；
        // console=顶部横排：隐藏组头、子项横向连排、回到横排页签样式。
        foreach (var (header, items) in new[] {
            (NavGroupGamesHeader, NavGroupGamesItems),
            (NavGroupDisplayHeader, NavGroupDisplayItems),
            (NavGroupSystemHeader, NavGroupSystemItems) })
        {
            header.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
            items.Orientation = studio ? Orientation.Vertical : Orientation.Horizontal;
        }
        foreach (var nav in AllNavButtons)
        {
            var style = studio
                ? (TopNavButtons.Contains(nav) ? "SideNavItemStyle" : "SideNavSubItemStyle")
                : "StudioNavStyle";
            nav.SetResourceReference(StyleProperty, style);
        }
    }

    private void OverviewMode_Click(object sender, RoutedEventArgs e)
    => SetDisplayMode(SettingsService.Current.OverviewMode == "console" ? "studio" : "console");

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = !MoreMenu.IsOpen;
    }

    internal void RefreshWallpaper()
    {
        WallpaperBackdrop.Visibility = Visibility.Collapsed;
        WallpaperBackdrop.Source = null;
        if (!SettingsService.Current.WallpaperEnabled) return;
        try
        {
            WallpaperBackdrop.Source = WallpaperService.Load();
            WallpaperBackdrop.Opacity = WallpaperService.NormalizeOpacity(SettingsService.Current.WallpaperOpacity);
            if (WallpaperBackdrop.Source is not null) WallpaperBackdrop.Visibility = Visibility.Visible;
        }
        catch (Exception) { /* An unavailable personal backdrop does not block opening the application. */ }
    }

    internal void SetDisplayMode(string mode)
    {
        SettingsService.Current.OverviewMode = mode == "console" ? "console" : "studio";
        try { SettingsService.Save(SettingsService.Current); }
        catch (Exception ex) { DialogService.Warning("界面模式", "本次切换已生效，但无法保存偏好：" + ex.Message); }
        UpdateOverviewModeLabel();
        ThemeManager.SetMode(SettingsService.Current.ThemeMode);
        if (NavHome.IsChecked == true) SwitchPage(GetPage("home"));
        if (_pageCache.TryGetValue("settings", out var settings) && settings is SettingsView settingsView) settingsView.RefreshDisplayMode();
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

    internal void ReviewSelection(IEnumerable<string> ids)
    {
        ShowOptimizePage();
        ((OptimizeView)GetPage("opt")).SelectForReview(ids);
    }

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

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_pageFactories is null)
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

    /// <summary>侧栏"掉帧修复"动作项：进滤镜页并打开修复窗口。</summary>
    private void NavDeltaFix_Click(object sender, RoutedEventArgs e) => OpenDeltaFixFromHome();

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

    private void CaptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;
        switch (action)
        {
            case "min":
                SystemCommands.MinimizeWindow(this);
                break;
            case "max":
                if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
                else SystemCommands.MaximizeWindow(this);
                break;
            case "close":
                RequestUserClose();
                break;
        }
    }

    internal void OpenDeltaFixFromHome()
    {
        NavigateTo("filters");
        Dispatcher.BeginInvoke(() => { if (GetPage("filters") is FilterView filter) filter.OpenDeltaFix(); });
    }

    public void NavigateTo(string key)
    {
        switch (key)
        {
            case "home": NavHome.IsChecked = true; break;
            case "games": NavGames.IsChecked = true; break;
            case "quick": NavQuick.IsChecked = true; break;
            case "detect": NavDetect.IsChecked = true; break;
            case "opt": NavOpt.IsChecked = true; break;
            case "display": NavDisplay.IsChecked = true; break;
            case "filters": NavFilters.IsChecked = true; break;
            case "session": NavSession.IsChecked = true; break;
            case "ab": NavAb.IsChecked = true; break;
            case "backup": NavBackup.IsChecked = true; break;
            case "tools": NavTools.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
        }
    }

    public void RefreshHomeContacts()
    {
        // Legacy contact fields remain readable; community feedback is now a global entry.
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => ShowGuideCard();
    private void Feedback_Click(object sender, RoutedEventArgs e) => ShowCommunityPrompt();

}
