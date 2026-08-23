using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using DeltaForceTune.Wpf.Services;
using DeltaForceTune.Wpf.Views;

namespace DeltaForceTune.Wpf;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages;
    private bool _themeAnimating;

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
        ThemeManager.Initialize();
        ThemeManager.SetMode("dark");
        ThemeDark.IsChecked = true;
    }

    public void ShowOptimizePage()
    {
        NavOpt.IsChecked = true;
        SwitchPage(_pages["opt"]);
        if (_pages["opt"] is OptimizeView opt)
            opt.ReloadFromState();
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
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        PageHost.BeginAnimation(OpacityProperty, fade);
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

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode })
        {
            AnimateThemeChange(mode);
        }
    }

    private void AnimateThemeChange(string mode)
    {
        if (_themeAnimating)
            return;

        _themeAnimating = true;
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
        fadeOut.Completed += (_, _) =>
        {
            ThemeManager.SetMode(mode);
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            fadeIn.Completed += (_, _) => _themeAnimating = false;
            BeginAnimation(OpacityProperty, fadeIn);
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
