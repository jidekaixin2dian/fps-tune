using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

public partial class MainWindow : Window
{
    // 页面按需创建、创建后缓存：启动只构建首页，显著降低冷启动时间与常驻内存。
    private readonly Dictionary<string, Func<UserControl>> _pageFactories;
    private readonly Dictionary<string, UserControl> _pageCache = new();
    private readonly TranslateTransform _pageSlide = new(24, 0);

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

    public MainWindow()
    {
        InitializeComponent();
        SettingsService.Load();

        _pageFactories = new Dictionary<string, Func<UserControl>>
        {
            ["home"] = () => new HomeView(),
            ["console"] = () => new ConsoleView(),
            ["detect"] = () => new DetectView(),
            ["opt"] = () => new OptimizeView(),
            ["session"] = () => new SessionView(),
            ["ab"] = () => new AbExperimentView(),
            ["friend"] = () => new FriendTestView(),
            ["backup"] = () => new BackupLogView(),
            ["settings"] = () => new SettingsView(),
        };

        PageHost.RenderTransform = _pageSlide;
        // 初始首页不走滑入动画: 若保留 X=24, 首页会永久右偏 24px,
        // 右侧页边距被吃掉(联系条顶到窗口边框)——"首次打开界面残缺"的根因。
        // SwitchPage 的动画显式 From=24, 不受此处归零影响。
        _pageSlide.X = 0;
        PageHost.Content = GetPage("home");
        // 托盘图标随应用启动常驻(与最小化设置无关); 设置只控制最小化行为
        TrayService.EnsureCreated();

        // 版本号唯一来源：程序集（编译自 Directory.Build.props）
        var ver = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        TitleVersionText.Text = ver;
        SidebarVersionText.Text = ver;
        AdminStatusText.Text = AdminHelper.IsAdministrator() ? "管理员模式" : "普通用户";
        UpdateOverviewModeLabel();

        var theme = SettingsService.Current.ThemeMode;
        if (string.IsNullOrWhiteSpace(theme))
            theme = "dark";
        ThemeManager.Initialize();
        ThemeManager.SetMode(theme);
        ApplyAuroraSetting();

        StateChanged += OnStateChanged;
        ChromeGrid.SizeChanged += (_, _) => UpdateRootClip();
        Closing += (_, _) =>
        {
            // 只取消本窗口 A/B 页面启动的脚本；PowerShellRunner 会结束其子进程树，
            // 不按名称影响正式安装版或其他用户进程。RunningStep 留在磁盘时可在下次启动恢复。
            if (_pageCache.TryGetValue("ab", out var page) && page is AbExperimentView ab)
                ab.CancelPendingRun();
        };
        Closed += (_, _) =>
        {
            TrayService.Dispose();
            if (_hwndSource is not null)
                UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);

        var pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwndSource!.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        ApplyHotkeyRegistration();
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint NativeGetDpiForWindow(nint hwnd);

    /// <summary>按设置应用/移除整窗极光光场, 并即时切换低配模式下的动效。</summary>
    public void ApplyAuroraSetting()
    {
        var aurora = ThemeManager.CurrentAuroraBrushes;
        var enabled = SettingsService.Current.AuroraEnabled && aurora is not null;
        AuroraLayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            StopAuroraMotion();
            return;
        }

        AuroraMainLayer.Background = aurora!.Main;
        AuroraSideLayer.Background = aurora.Side;
        AuroraReflectionLayer.Background = aurora.Reflection;
        RefreshAuroraMotion();
    }

    /// <summary>切换主题或低配开关后重置三层极光的静态位置与慢速动效。</summary>
    private void RefreshAuroraMotion()
    {
        StopAuroraMotion();
        if (AuroraLayer.Visibility != Visibility.Visible)
            return;

        if (UiPerformance.LowSpec)
            return;

        AuroraMainTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-16, 16, TimeSpan.FromSeconds(34))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        AuroraMainTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-4, 8, TimeSpan.FromSeconds(41))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        AuroraSideTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(10, -14, TimeSpan.FromSeconds(38))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        AuroraSideTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-10, 12, TimeSpan.FromSeconds(46))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        AuroraReflectionTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-12, 14, TimeSpan.FromSeconds(44))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });

    }

    private void StopAuroraMotion()
    {
        AuroraMainTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AuroraMainTransform.BeginAnimation(TranslateTransform.YProperty, null);
        AuroraSideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AuroraSideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        AuroraReflectionTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AuroraReflectionTransform.BeginAnimation(TranslateTransform.YProperty, null);
        AuroraMainTransform.X = 0;
        AuroraMainTransform.Y = 0;
        AuroraSideTransform.X = 0;
        AuroraSideTransform.Y = 0;
        AuroraReflectionTransform.X = 0;
        AuroraReflectionTransform.Y = 0;
    }

    private HwndSource? _hwndSource;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
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
            WindowState = WindowState.Normal;
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
        if (key == "home" && SettingsService.Current.OverviewMode != "classic") key = "console";
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

    private void UpdateOverviewModeLabel()
    {
        var classic = SettingsService.Current.OverviewMode == "classic";
        OverviewModeButton.Content = classic ? "切换控制台界面" : "切换经典界面";
        TitleBar.SetResourceReference(Panel.BackgroundProperty, classic ? "SidebarBackgroundBrush" : "ConsoleBackgroundBrush");
        TabBar.SetResourceReference(Panel.BackgroundProperty, classic ? "SidebarBackgroundBrush" : "ConsoleBackgroundBrush");
    }

    private void OverviewMode_Click(object sender, RoutedEventArgs e)
    => SetDisplayMode(SettingsService.Current.OverviewMode == "classic" ? "console" : "classic");

    internal void SetDisplayMode(string mode)
    {
        SettingsService.Current.OverviewMode = mode == "classic" ? "classic" : "console";
        try { SettingsService.Save(SettingsService.Current); }
        catch (Exception ex) { DialogService.Warning("界面模式", "本次切换已生效，但无法保存偏好：" + ex.Message); }
        UpdateOverviewModeLabel();
        if (_pageCache.TryGetValue("detect", out var detect) && detect is DetectView view) view.ApplyDisplayMode();
        if (NavHome.IsChecked == true) SwitchPage(GetPage("home"));
        if (_pageCache.TryGetValue("settings", out var settings) && settings is SettingsView settingsView) settingsView.RefreshDisplayMode();
    }

    internal Task<bool> RefreshDetectionAsync() => ((DetectView)GetPage("detect")).RunDetectionAsync();

    internal void ReviewSelection(IEnumerable<string> ids)
    {
        ShowOptimizePage();
        ((OptimizeView)GetPage("opt")).SelectForReview(ids);
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
                DialogService.Warning("检测并优化", "检测未完成。请检查检测页的报错信息后重试。");
                return;
            }
        }

        // 第二步：检测完成后引导用户进入优化页。
        ShowOptimizePage();
        DialogService.Info("检测并优化", "检测已完成。请选择左侧预设或勾选需要的优化项，确认后点击“应用”。");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_pageFactories is null)
            return;
        if (sender is RadioButton { Tag: string key } && _pageFactories.ContainsKey(key))
        {
            // 侧栏导航分布在多个容器中（WPF 单选钮按逻辑父容器分组，跨容器不互斥），
            // 这里手动保证全组唯一选中：修复"点过设置后其他按钮无法熄灭它、再点设置无响应"。
            foreach (var radio in new[] { NavHome, NavDetect, NavOpt, NavSession, NavAb, NavFriend, NavBackup, NavSettings })
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

        // 低配模式: 跳过滑入/淡入动画
        if (UiPerformance.LowSpec)
        {
            PageHost.Opacity = 1;
            PageHost.Content = page;
            return;
        }

        PageHost.Opacity = 0;
        PageHost.Content = page;

        var slideAnim = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        _pageSlide.BeginAnimation(TranslateTransform.XProperty, slideAnim);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        PageHost.BeginAnimation(OpacityProperty, fade);
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
                Close();
                break;
        }
    }

    public void NavigateTo(string key)
    {
        switch (key)
        {
            case "home": NavHome.IsChecked = true; break;
            case "detect": NavDetect.IsChecked = true; break;
            case "opt": NavOpt.IsChecked = true; break;
            case "session": NavSession.IsChecked = true; break;
            case "ab": NavAb.IsChecked = true; break;
            case "friend": NavFriend.IsChecked = true; break;
            case "backup": NavBackup.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
        }
    }

    public void RefreshHomeContacts()
    {
        if (GetPage("home") is HomeView home)
            home.RefreshContacts();
    }
}
