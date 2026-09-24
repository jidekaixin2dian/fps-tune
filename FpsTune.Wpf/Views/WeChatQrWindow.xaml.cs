using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class WeChatQrWindow : Window
{
    public WeChatQrWindow()
    {
        InitializeComponent();
        var s = SettingsService.Current;
        if (!string.IsNullOrWhiteSpace(s.WeChat))
            WeChatIdText.Text = Str.T("Str.WeChatIdLabel") + s.WeChat;
    }

    private void CopyWeChat_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(SettingsService.Current.WeChat);
        DialogService.Info(Str.T("Str.AppName"), Str.T("Str.WeChatIdCopied"));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DragMove_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
