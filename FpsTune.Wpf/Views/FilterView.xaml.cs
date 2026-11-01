using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class FilterView : UserControl
{
    public FilterView() { InitializeComponent(); }
    private void OpenDeltaFix_Click(object sender, RoutedEventArgs e) => OpenDeltaFix();
    internal void OpenDeltaFix()
    {
        var dialog = new DeltaFrameFixWindow { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        dialog.ShowDialog();
    }
}
