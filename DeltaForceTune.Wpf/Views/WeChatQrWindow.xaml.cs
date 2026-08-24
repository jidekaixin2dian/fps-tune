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
        DialogService.Info("三角洲帧律", "微信号已复制。");
    }
}
