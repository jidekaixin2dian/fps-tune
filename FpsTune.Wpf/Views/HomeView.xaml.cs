using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using System.Linq;

namespace FpsTune.Wpf.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshContacts();
        VersionText.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        // 优化项数量随 catalog 增长，首页文案不写死
        OptCardSummary.Text = $"预设与逐项开关、{ItemCatalog.All.Count} 项系统层优化、一键还原";
        var hw = Core.HardwareInfoService.Get();
        HardwareSummaryText.Text = $"{hw.Cpu}  |  {hw.Gpu}  |  {hw.RamGB:0.#} GB";

        // 新手引导入口只在首次打开时显示。
        if (StateStore.HasSeenOnboarding)
            StartOptimizeButton.Visibility = Visibility.Collapsed;
    }

    public void RefreshContacts()
    {
        var s = SettingsService.Current;
        var wechat = "请点击查看二维码";
        var qq = string.IsNullOrWhiteSpace(s.QQ) ? "待设置" : s.QQ;
        var douyin = string.IsNullOrWhiteSpace(s.Douyin) ? "待设置" : s.Douyin;

        var qqLink = s.QQLink;
        if ((string.IsNullOrWhiteSpace(qqLink) || qqLink.Contains("wpa.qq.com") || qqLink.Contains("tencent://")) && !string.IsNullOrWhiteSpace(s.QQ))
            qqLink = "https://user.qzone.qq.com/" + Uri.EscapeDataString(s.QQ);

        ContactPanel.Children.Clear();
        ContactPanel.Children.Add(MakeContact("微信", wechat, ""));
        ContactPanel.Children.Add(MakeContact("QQ", qq, qqLink));
        ContactPanel.Children.Add(MakeContact("抖音", douyin, s.DouyinLink));

        if (!string.IsNullOrWhiteSpace(s.Email))
            ContactPanel.Children.Add(MakeContact("邮箱", s.Email, ""));

        ContactPanel.Children.Add(new TextBlock
        {
            Text = "仅接受合作/反馈",
            Margin = new Thickness(0, 0, 0, 0),
            FontSize = 11,
            Opacity = 0.7,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"]
        });
    }

    private TextBlock MakeContact(string label, string value, string link)
    {
        var text = new TextBlock
        {
            Text = $"{label}：{value}",
            Margin = new Thickness(0, 0, 16, 0),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondaryBrush"]
        };
        if (!string.IsNullOrWhiteSpace(link))
            text.TextDecorations = TextDecorations.Underline;
        text.MouseLeftButtonUp += (_, _) => OpenContact(label, value, link);
        return text;
    }

    private static void OpenContact(string label, string value, string link)
    {
        if (label == "微信" && string.IsNullOrWhiteSpace(link))
        {
            var owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive);
            var qr = new WeChatQrWindow();
            if (owner is not null)
                qr.Owner = owner;
            qr.ShowDialog();
            return;
        }

        if (!string.IsNullOrWhiteSpace(link))
        {
            try
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            }
            catch
            {
                DialogService.Warning("FPS 帧律", "无法打开链接。");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(value) && value != "待设置")
        {
            Clipboard.SetText(value);
            DialogService.Info("FPS 帧律", $"已复制：{value}");
        }
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
        StateStore.MarkOnboardingSeen();
        StartOptimizeButton.Visibility = Visibility.Collapsed;

        if (Window.GetWindow(this) is MainWindow main)
            _ = main.RunOnboardingAsync();
    }
}
