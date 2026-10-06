using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class WeChatQrWindow : Window
{
    private readonly string _weChatId;

    public WeChatQrWindow()
    {
        InitializeComponent();
        var s = SettingsService.Current;
        _weChatId = string.IsNullOrWhiteSpace(s.WeChat) ? "jiaxindeyang" : s.WeChat;
        WeChatIdText.Text = Str.T("Str.WeChatIdLabel") + _weChatId;
    }

    private void CopyWeChat_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_weChatId);
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
