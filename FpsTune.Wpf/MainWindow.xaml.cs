using System.Reflection;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages;
    
    public MainWindow()
    {
        InitializeComponent();
        SettingsService.Load();

        _pages = new Dictionary<string, UserControl>
        {
            ["home"] = new HomeView(),
            ["detect"] = new DetectView(),
            ["opt"] = new OptimizeView(),
            ["ab"] = new AbExperimentView(),
            ["friend"] = new FriendTestView(),
            ["backup"] = new BackupLogView(),
            ["settings"] = new SettingsView(),
        };

        PageHost.Content = _pages["home"];

        // 版本号唯一来源：程序集（编译自 Directory.Build.props）
        var ver = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        TitleVersionText.Text = ver;
        SidebarVersionText.Text = ver;

        var theme = SettingsService.Current.ThemeMode;
        if (string.IsNullOrWhiteSpace(theme))
            theme = "dark";
        ThemeManager.Initialize();
        ThemeManager.SetMode(theme);
    }

    public void ShowOptimizePage()
    {
        NavOpt.IsChecked = true;
        SwitchPage(_pages["opt"]);
        if (_pages["opt"] is OptimizeView opt)
            opt.ReloadFromState();
    }

    public async Task RunOnboardingAsync()
    {
        // 第一步：先去检测页完成一次检测。
        NavigateTo("detect");
        if (_pages["detect"] is DetectView detect)
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
        if (_pages is null)
            return;
        if (sender is RadioButton { Tag: string key } && _pages.TryGetValue(key, out var page))
        {
            SwitchPage(page);
            if (key == "opt" && page is OptimizeView opt)
                opt.ReloadFromState();
        }
    }

    private void SwitchPage(UserControl page)
    {
        if (ReferenceEquals(PageHost.Content, page))
            return;

        PageHost.Opacity = 0;
        PageHost.Content = page;

        var slide = new TranslateTransform(24, 0);
        PageHost.RenderTransform = slide;
        var slideAnim = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        slide.BeginAnimation(TranslateTransform.XProperty, slideAnim);

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
        if (_pages["home"] is HomeView home)
            home.RefreshContacts();
    }
}
