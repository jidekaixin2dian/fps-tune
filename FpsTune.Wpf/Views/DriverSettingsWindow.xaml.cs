using System.Windows;
using System.Windows.Input;

namespace FpsTune.Wpf.Views;

public partial class DriverSettingsWindow : Window
{
    public DriverSettingsWindow()
    {
        InitializeComponent(); DriverSettings.ShowTargetLabel();
        Closing += (_, e) => { if (DriverSettings.IsBusy) e.Cancel = true; };
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
