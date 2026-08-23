using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshContacts();
    }

    public void RefreshContacts()
    {
        var s = SettingsService.Current;
        var wechat = string.IsNullOrWhiteSpace(s.WeChat) ? "待设置" : s.WeChat;
        var qq = string.IsNullOrWhiteSpace(s.QQ) ? "待设置" : s.QQ;
        var douyin = string.IsNullOrWhiteSpace(s.Douyin) ? "待设置" : s.Douyin;

        ContactPanel.Children.Clear();
        ContactPanel.Children.Add(MakeContact("微信", wechat));
        ContactPanel.Children.Add(MakeContact("QQ", qq));
        ContactPanel.Children.Add(MakeContact("抖音", douyin));

        if (!string.IsNullOrWhiteSpace(s.Email))
            ContactPanel.Children.Add(MakeContact("邮箱", s.Email));
    }

    private static TextBlock MakeContact(string label, string value)
    {
        var text = new TextBlock
        {
            Text = $"{label}：{value}",
            Margin = new Thickness(0, 0, 16, 0),
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondaryBrush"]
        };
        return text;
    }

    private void Module_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string key } &&
            Window.GetWindow(this) is MainWindow main)
        {
            main.NavigateTo(key);
        }
    }

    private void StartOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.NavigateTo("opt");
    }
}
