using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
        VersionText.Text = "版本：v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        RefreshAdminStatus();
    }

    private void LoadSettings()
    {
        var s = SettingsService.Current;

        if (s.ThemeMode == "light")
            ThemeLightRadio.IsChecked = true;
        else if (s.ThemeMode == "system")
            ThemeSystemRadio.IsChecked = true;
        else
            ThemeDarkRadio.IsChecked = true;

        RefreshAdminStatus();
    }

    private void RefreshAdminStatus()
    {
        var isAdmin = AdminHelper.IsAdministrator();
        if (isAdmin)
        {
            AdminStatusText.Text = "当前已是管理员";
            AdminStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["OkBrush"];
            AdminRestartButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminStatusText.Text = "当前不是管理员，部分优化项可能失败";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDarkRadio.IsChecked == true ? "dark"
            : ThemeLightRadio.IsChecked == true ? "light" : "system";

        var settings = SettingsService.Current;
        settings.ThemeMode = theme;
        SettingsService.Save(settings);

        MessageBox.Show("设置已保存。", "三角洲帧律", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AdminRestartButton_Click(object sender, RoutedEventArgs e)
        => AdminHelper.RestartAsAdministrator();
}
