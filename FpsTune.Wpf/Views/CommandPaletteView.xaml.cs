using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class CommandPaletteView : UserControl
{
    private readonly WorkspaceCommands _commands = new();
    public event Action<WorkspaceCommand>? CommandChosen;
    public event Action? Dismissed;
    public CommandPaletteView() { InitializeComponent(); }
    internal void Open()
    {
        QueryBox.Clear(); RefreshResults();
        Dispatcher.BeginInvoke(() => { QueryBox.Focus(); Keyboard.Focus(QueryBox); });
    }
    private void Query_Changed(object sender, TextChangedEventArgs e) { if (ResultsList is not null) RefreshResults(); }
    internal void RefreshLanguage() => RefreshResults();
    private void RefreshResults()
    {
        var results = _commands.Search(QueryBox.Text);
        ResultsList.ItemsSource = results;
        ResultsList.SelectedIndex = results.Count > 0 ? 0 : -1;
        EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Palette_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Dismissed?.Invoke(); e.Handled = true; }
        else if (e.OriginalSource is Button) return;
        else if (e.Key == Key.Enter) { Choose(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = (ResultsList.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + ResultsList.Items.Count) % ResultsList.Items.Count;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem); e.Handled = true;
        }
    }
    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not WorkspaceCommand command) return;
        ResultsList.SelectedItem = command; Choose(); e.Handled = true;
    }
    private void Choose()
    {
        if (ResultsList.SelectedItem is not WorkspaceCommand command) return;
        _commands.Remember(command.Id); CommandChosen?.Invoke(command);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke();
}
