using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;
using DeltaForceTune.Wpf.Views;

namespace DeltaForceTune.Wpf;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages;

    public MainWindow()
    {
        InitializeComponent();

        _pages = new Dictionary<string, UserControl>
        {
            ["detect"] = new DetectView(),
            ["opt"] = new OptimizeView(),
            ["ab"] = new AbExperimentView(),
            ["friend"] = new FriendTestView(),
            ["backup"] = new BackupLogView(),
        };

        PageHost.Content = _pages["detect"];
        ThemeManager.Initialize();
        ThemeManager.SetMode("dark");
        ThemeDark.IsChecked = true;
    }

    public void ShowOptimizePage()
    {
        NavOpt.IsChecked = true;
        PageHost.Content = _pages["opt"];
        if (_pages["opt"] is OptimizeView opt)
            opt.ReloadFromState();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_pages is null)
            return;
        if (sender is RadioButton { Tag: string key } && _pages.TryGetValue(key, out var page))
        {
            PageHost.Content = page;
        }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode })
        {
            ThemeManager.SetMode(mode);
        }
    }
}
