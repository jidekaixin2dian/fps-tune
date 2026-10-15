using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class AppDialogWindow : Window
{
    public AppDialogWindow(string title, string message, bool isConfirm, bool isDanger, string confirmText, string cancelText)
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            // 标准对话框键盘习惯：Esc=取消，Enter=主按钮（确认/知道了）
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                DialogResult = true;
                e.Handled = true;
            }
        };

        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        if (!isConfirm)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }

        if (isDanger)
        {
            CopyDetailsButton.Visibility = isConfirm ? Visibility.Collapsed : Visibility.Visible;
            StatusIconSurface.Background = Brushes.Transparent;
            StatusIconSurface.BorderBrush = (Brush)Application.Current.Resources["DangerBrush"];
            IconPath.Stroke = (Brush)Application.Current.Resources["DangerBrush"];
            IconPath.Data = Geometry.Parse("M 10,2 L 19,18 H 1 Z M 10,7 V 11 M 10,14 L 10,14.1");
        }
        if (isDanger && isConfirm)
        {
            ConfirmButton.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
            ConfirmButton.Background = (Brush)Application.Current.Resources["DangerBrush"];
            ConfirmButton.BorderBrush = Brushes.Transparent;
            ConfirmButton.Foreground = Brushes.White;
        }
    }

    internal Action<string> CopyDetails { get; set; } = Clipboard.SetText;

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        try { CopyDetails(TitleTextBlock.Text + "\n\n" + MessageTextBlock.Text); CopyDetailsButton.Content = Str.T("Str.ErrorCopied"); }
        catch { CopyDetailsButton.Content = Str.T("Str.ErrorCopyFailed"); }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
