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
    }

    private void LoadSettings()
    {
        var s = SettingsService.Current;
        WeChatBox.Text = s.WeChat;
        WeChatLinkBox.Text = s.WeChatLink;
        QQBox.Text = s.QQ;
        QQLinkBox.Text = s.QQLink;
        DouyinBox.Text = s.Douyin;
        DouyinLinkBox.Text = s.DouyinLink;
        EmailBox.Text = s.Email;

        if (s.ThemeMode == "light")
            ThemeLightRadio.IsChecked = true;
        else if (s.ThemeMode == "system")
            ThemeSystemRadio.IsChecked = true;
        else
            ThemeDarkRadio.IsChecked = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDarkRadio.IsChecked == true ? "dark"
            : ThemeLightRadio.IsChecked == true ? "light" : "system";

        var settings = new AppSettings
        {
            WeChat = WeChatBox.Text.Trim(),
            WeChatLink = WeChatLinkBox.Text.Trim(),
            QQ = QQBox.Text.Trim(),
            QQLink = QQLinkBox.Text.Trim(),
            Douyin = DouyinBox.Text.Trim(),
            DouyinLink = DouyinLinkBox.Text.Trim(),
            Email = EmailBox.Text.Trim(),
            ThemeMode = theme
        };

        SettingsService.Save(settings);

        if (Window.GetWindow(this) is MainWindow main)
            main.RefreshHomeContacts();

        MessageBox.Show("设置已保存。", "三角洲帧律", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
