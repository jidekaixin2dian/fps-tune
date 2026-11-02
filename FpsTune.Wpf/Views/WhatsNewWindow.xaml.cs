using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>更新完成后的「这个版本改了什么」。内容来自安装前记录的 Release 说明，不在这里编。</summary>
public partial class WhatsNewWindow : Window
{
    internal WhatsNewWindow(WhatsNewNotice notice)
    {
        InitializeComponent();
        VersionText.Text = Str.T("Str.WhatsNewVersion", notice.Version);
        NotesText.Text = ReleaseNotesText.Format(notice.Notes);
        AutomationProperties.SetName(OkButton, Str.T("Str.WhatsNewClose"));
    }

    /// <summary>有未读说明才弹；关掉之后记成已读，下一个版本再弹。</summary>
    internal static void ShowIfPending(Window? owner)
    {
        if (WhatsNewService.Pending() is not { } notice) return;
        var window = new WhatsNewWindow(notice) { Owner = owner };
        DialogService.PrepareWindowForTest?.Invoke(window);
        try { window.ShowDialog(); }
        finally { WhatsNewService.MarkSeen(notice); }
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { /* 鼠标已释放 */ }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
