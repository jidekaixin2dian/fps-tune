using System.Windows;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class CloseChoiceWindow : Window
{
    internal CloseChoice? Choice { get; private set; }
    public CloseChoiceWindow()
    {
        InitializeComponent();
        TrayChoice.Checked += (_, _) => ConfirmButton.SetResourceReference(ContentProperty, "Str.CloseChoiceTray");
        ExitChoice.Checked += (_, _) => ConfirmButton.SetResourceReference(ContentProperty, "Str.CloseChoiceExit");
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Choice = new(ExitChoice.IsChecked == true ? CloseBehavior.Exit : CloseBehavior.Tray, RememberChoice.IsChecked == true);
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
