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
            IconTint.SetResourceReference(Border.BackgroundProperty, "DangerBrush");
            IconPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DangerBrush");
            IconDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DangerBrush");
            IconPath.Data = Geometry.Parse("M 10.3,3.9 C 11.05,2.6 12.95,2.6 13.7,3.9 L 22.2,18.5 C 22.95,19.8 22,21.5 20.5,21.5 H 3.5 C 2,21.5 1.05,19.8 1.8,18.5 Z M 12,8.5 V 13");
            Canvas.SetTop(IconDot, 16.2);
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
