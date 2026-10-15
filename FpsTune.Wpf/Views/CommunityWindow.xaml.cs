using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class CommunityWindow : Window
{
    private readonly Action _suppressReminder;
    private readonly Action<string> _openLink;
    public CommunityWindow() : this(new CommunityPreferences().Suppress, ProductLinks.Open) { }
    internal CommunityWindow(Action suppressReminder, Action<string> openLink)
    {
        _suppressReminder = suppressReminder; _openLink = openLink;
        InitializeComponent();
        Closing += SavePreference;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    private void SavePreference(object? sender, CancelEventArgs e)
    {
        if (NeverRemindCheck.IsChecked != true) return;
        try { _suppressReminder(); }
        catch { e.Cancel = true; ShowError("Str.CommunitySaveFailed"); }
    }
    private void ShowError(string key) { StatusText.Text = Str.T(key); StatusText.Visibility = Visibility.Visible; }
    private void Later_Click(object sender, RoutedEventArgs e) => Close();
    private void Join_Click(object sender, RoutedEventArgs e) => OpenLink(ProductLinks.CommunityUrl);
    private void Issue_Click(object sender, RoutedEventArgs e) => OpenLink(ProductLinks.IssueUrl);
    private void OpenLink(string url)
    {
        try { _openLink(url); }
        catch { ShowError("Str.CannotOpenLink"); return; }
        Close();
    }
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
