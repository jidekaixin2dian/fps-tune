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
        // WMI 查询较慢，放后台线程避免阻塞首帧
        HardwareSummaryText.Text = "--";
        Loaded += async (_, _) =>
        {
            var hw = await Task.Run(Core.HardwareInfoService.Get);
            HardwareSummaryText.Text = $"{hw.Cpu}  |  {hw.Gpu}  |  {hw.RamGB:0.#} GB";
        };

        // 新手引导入口只在首次打开时显示。
        if (StateStore.HasSeenOnboarding)
            StartOptimizeButton.Visibility = Visibility.Collapsed;
    }

    public void RefreshContacts()
    {
        var s = SettingsService.Current;
        var wechat = Str.T("Str.ClickToSeeQr");
        var qq = string.IsNullOrWhiteSpace(s.QQ) ? Str.T("Str.StatusToSet") : s.QQ;
        var douyin = string.IsNullOrWhiteSpace(s.Douyin) ? Str.T("Str.StatusToSet") : s.Douyin;

        var qqLink = s.QQLink;
        if ((string.IsNullOrWhiteSpace(qqLink) || qqLink.Contains("wpa.qq.com") || qqLink.Contains("tencent://")) && !string.IsNullOrWhiteSpace(s.QQ))
            qqLink = "https://user.qzone.qq.com/" + Uri.EscapeDataString(s.QQ);

        ContactPanel.Children.Clear();
        ContactPanel.Children.Add(MakeContact(Str.T("Str.WeChat"), wechat, ""));
        ContactPanel.Children.Add(MakeContact("QQ", qq, qqLink));
        ContactPanel.Children.Add(MakeContact("抖音", douyin, s.DouyinLink));

        if (!string.IsNullOrWhiteSpace(s.Email))
            ContactPanel.Children.Add(MakeContact(Str.T("Str.Email"), s.Email, ""));

        ContactPanel.Children.Add(new TextBlock
        {
            Text = Str.T("Str.WeChatPurpose"),
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
        if (label == Str.T("Str.WeChat") && string.IsNullOrWhiteSpace(link))
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

        // settings.json 是用户可改写的持久化数据，UseShellExecute 对任意路径/协议生效；
        // 只放行 http(s)，防止文件被改成可执行路径后一点即启动。
        if (!string.IsNullOrWhiteSpace(link)
            && Uri.TryCreate(link, UriKind.Absolute, out var linkUri)
            && linkUri.Scheme is "http" or "https")
        {
            try
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            }
            catch
            {
                DialogService.Warning(Str.T("Str.AppName"), Str.T("Str.CannotOpenLink"));
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(value) && value != Str.T("Str.StatusToSet"))
        {
            Clipboard.SetText(value);
            DialogService.Info(Str.T("Str.AppName"), $"已复制：{value}");
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
