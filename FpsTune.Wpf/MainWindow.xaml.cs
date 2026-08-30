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

    // Windows 11 会按系统圆角(约 8px)裁剪窗口并绘制系统阴影，与自绘 10px 圆角
    // 叠加后四角出现白色缺口与断线；显式关闭系统圆角，形状完全交给 WPF 透明合成。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

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
            ["detect"] = () => new DetectView(),
            ["opt"] = () => new OptimizeView(),
            ["ab"] = () => new AbExperimentView(),
            ["friend"] = () => new FriendTestView(),
            ["backup"] = () => new BackupLogView(),
            ["settings"] = () => new SettingsView(),
        };

        Services.StartupTrace.Log("ctor: after InitializeComponent");
        PageHost.RenderTransform = _pageSlide;
        // 初始首页不走滑入动画: 若保留 X=24, 首页会永久右偏 24px,
        // 右侧页边距被吃掉(联系条顶到窗口边框)——"首次打开界面残缺"的根因。
        // SwitchPage 的动画显式 From=24, 不受此处归零影响。
        _pageSlide.X = 0;
        PageHost.Content = GetPage("home");
        Services.StartupTrace.Log("ctor: after home page");
        // 托盘图标随应用启动常驻(与最小化设置无关); 设置只控制最小化行为
        TrayService.EnsureCreated();
        Services.StartupTrace.Log("ctor: after tray ensure");

        // 版本号唯一来源：程序集（编译自 Directory.Build.props）
        var ver = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        TitleVersionText.Text = ver;
        SidebarVersionText.Text = ver;

        var theme = SettingsService.Current.ThemeMode;
        if (string.IsNullOrWhiteSpace(theme))
            theme = "dark";
        ThemeManager.Initialize();
        ThemeManager.SetMode(theme);
        ApplyAuroraSetting(ThemeManager.CurrentGlowBrush);

        StateChanged += OnStateChanged;
        ChromeGrid.SizeChanged += (_, _) => UpdateRootClip();
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

        // 关闭 DWM 系统圆角（Win11）：防止系统按 8px 裁剪 10px 自绘圆角造成白角断线。
        var pref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwndSource!.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        ApplyHotkeyRegistration();
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint NativeGetDpiForWindow(nint hwnd);

    /// <summary>按设置应用/移除窗口级极光层; 设置页切换氛围光开关时调用。</summary>
    public void ApplyAuroraSetting(RadialGradientBrush? glow)
    {
        if (glow is not null)
            AuroraLayer.Background = glow;
        AuroraLayer.Visibility = glow is null
            ? Visibility.Collapsed
            : Visibility.Visible;
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

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateRootClip();
        if (WindowState == WindowState.Minimized && SettingsService.Current.MinimizeToTray)
        {
            // 先让最小化动画落定再决定去向: 托盘图标挂载成功才隐藏窗口,
            // 否则保持任务栏最小化(避免窗口隐藏后托盘也没有、失去入口)。
            Dispatcher.BeginInvoke(() =>
            {
                if (WindowState != WindowState.Minimized)
                    return;
                if (TrayService.EnsureCreated())
                {
                    Hide();
                    TrayService.ShowMinimizedHint();
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
        var radius = WindowState == WindowState.Maximized ? 0 : 10;
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
            foreach (var radio in new[] { NavHome, NavDetect, NavOpt, NavAb, NavFriend, NavBackup, NavSettings })
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

        var slideAnim = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        _pageSlide.BeginAnimation(TranslateTransform.XProperty, slideAnim);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
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
