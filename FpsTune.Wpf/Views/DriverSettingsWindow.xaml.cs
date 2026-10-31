using System.Windows;

namespace FpsTune.Wpf.Views;

public partial class DriverSettingsWindow : Window
{
    public DriverSettingsWindow()
    {
        InitializeComponent(); DriverSettings.ShowTargetLabel();
        Closing += (_, e) => { if (DriverSettings.IsBusy) e.Cancel = true; };
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
