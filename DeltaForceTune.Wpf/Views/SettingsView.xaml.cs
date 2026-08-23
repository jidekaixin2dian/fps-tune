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
    }

    private void LoadSettings()
    {
        var s = SettingsService.Current;
        WeChatBox.Text = s.WeChat;
        QQBox.Text = s.QQ;
        DouyinBox.Text = s.Douyin;
        EmailBox.Text = s.Email;

        var index = s.ThemeMode switch
        {
            "light" => 1,
            "system" => 2,
            _ => 0
        };
        ThemeBox.SelectedIndex = index;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            WeChat = WeChatBox.Text.Trim(),
            QQ = QQBox.Text.Trim(),
            Douyin = DouyinBox.Text.Trim(),
            Email = EmailBox.Text.Trim(),
            ThemeMode = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dark"
        };

        SettingsService.Save(settings);

        if (Window.GetWindow(this) is MainWindow main)
            main.RefreshHomeContacts();

        MessageBox.Show("设置已保存。", "三角洲帧律", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
