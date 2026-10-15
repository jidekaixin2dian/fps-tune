using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

public partial class MainWindow
{
    private CommandPaletteView? _commandPalette;
    private IInputElement? _commandPreviousFocus;
    private bool _commandPreviousPageEnabled;
    internal static readonly RoutedCommand QuickAccessCommand = new();
    private void InitializeCommandPalette()
    {
        CommandBindings.Add(new CommandBinding(QuickAccessCommand, (_, _) => OpenCommandPalette()));
        InputBindings.Add(new KeyBinding(QuickAccessCommand, new KeyGesture(Key.K, ModifierKeys.Control)));
        LangService.Changed += RefreshWorkspaceLanguage;
        Closed += (_, _) => LangService.Changed -= RefreshWorkspaceLanguage;
    }
    private void RefreshWorkspaceLanguage()
    {
        AdminStatusText.Text = AdminHelper.IsAdministrator() ? Str.T("Str.AdminMode") : Str.T("Str.NormalUser");
        UpdateOverviewModeLabel();
        if (CommandOverlay.Visibility == Visibility.Visible) _commandPalette?.RefreshLanguage();
    }
    private void CommandSearch_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();
    internal void OpenCommandPalette()
    {
        if (CommandOverlay.Visibility == Visibility.Visible) return;
        if (_commandPalette is null)
        {
            _commandPalette = new CommandPaletteView();
            _commandPalette.CommandChosen += ExecuteWorkspaceCommand;
            _commandPalette.Dismissed += () => CloseCommandPalette(true);
            CommandHost.Content = _commandPalette;
        }
        _commandPreviousFocus = Keyboard.FocusedElement;
        _commandPreviousPageEnabled = PageHost.IsEnabled;
        PageHost.IsEnabled = false;
        CommandOverlay.Visibility = Visibility.Visible;
        _commandPalette.Open();
    }
    private void CloseCommandPalette(bool restoreFocus)
    {
        if (CommandOverlay.Visibility != Visibility.Visible) return;
        CommandOverlay.Visibility = Visibility.Collapsed;
        PageHost.IsEnabled = _commandPreviousPageEnabled;
        if (restoreFocus)
        {
            if (_commandPreviousFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
            else CommandSearchButton.Focus();
        }
        _commandPreviousFocus = null;
    }
    private void CommandBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, CommandOverlay)) CloseCommandPalette(true);
    }
    internal void ExecuteWorkspaceCommand(WorkspaceCommand chosen)
    {
        // Dispatch only an ID from our current catalog; never interpret supplied command text.
        var command = WorkspaceCommands.Build().FirstOrDefault(c => c.Id == chosen.Id);
        if (command is null) return;
        CloseCommandPalette(false);
        NavigateTo(command.Route);
        var page = GetPage(command.Route);
        if (command.Target is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (page is OptimizeView optimize) optimize.RevealItem(command.Target);
            else if (page is SettingsView settings && settings.FindName("SettingsNav" + command.Target) is RadioButton button) button.IsChecked = true;
            else if (page is DisplayQualityView) SelectTaggedTab(page, command.Target);
        });
    }
    private static bool SelectTaggedTab(DependencyObject parent, string target)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is RadioButton { Tag: string tag } radio && tag == target) { radio.IsChecked = true; return true; }
            if (SelectTaggedTab(child, target)) return true;
        }
        return false;
    }
}
