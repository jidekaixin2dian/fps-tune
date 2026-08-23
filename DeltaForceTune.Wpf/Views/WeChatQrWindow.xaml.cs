using System.Windows;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class WeChatQrWindow : Window
{
    public WeChatQrWindow()
    {
        InitializeComponent();
        var s = SettingsService.Current;
        if (!string.IsNullOrWhiteSpace(s.WeChat))
            WeChatIdText.Text = "微信号：" + s.WeChat;
    }

    private void CopyWeChat_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(SettingsService.Current.WeChat);
        MessageBox.Show("微信号已复制。", "三角洲帧律", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
