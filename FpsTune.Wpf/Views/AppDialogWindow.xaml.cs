using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace FpsTune.Wpf.Views;

public partial class AppDialogWindow : Window
{
    public AppDialogWindow(string title, string message, bool isConfirm, bool isDanger, string confirmText, string cancelText)
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
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
            ConfirmButton.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
            ConfirmButton.Background = (Brush)Application.Current.Resources["DangerBrush"];
            ConfirmButton.BorderBrush = Brushes.Transparent;
            ConfirmButton.Foreground = Brushes.White;
            IconText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            IconText.Text = "\uE7BA";
        }
        else
        {
            IconText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
        }
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
