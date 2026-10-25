using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ResolutionPanel
{
    private Window? _trialPrompt;

    private void ShowTrialPrompt()
    {
        // A low resolution can make the main window larger than the desktop, especially at high DPI.
        // Keep confirmation reachable in a small owned window; background render checks never activate it.
        if (_trialPrompt is not null || _owner is null || _owner.ShowActivated == false) return;
        var body = new StackPanel();
        TextBlock Label(TextBlock source, int size)
        {
            var label = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = source });
            body.Children.Add(label); return label;
        }
        Label(PendingText, 17); Label(CountdownText, 15);
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        KeyboardNavigation.SetTabNavigation(actions, KeyboardNavigationMode.Cycle);
        Button Action(Button source, string style, RoutedEventHandler click)
        {
            var button = new Button { Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 8, 0) };
            button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Button.Content)) { Source = source });
            button.SetBinding(IsEnabledProperty, new Binding(nameof(Button.IsEnabled)) { Source = source });
            button.Click += click; actions.Children.Add(button); return button;
        }
        Action(KeepButton, "PrimaryButtonStyle", KeepClick);
        var revert = Action(RevertButton, "SecondaryButtonStyle", RestoreClick); body.Children.Add(actions);
        var surface = new Border { Padding = new Thickness(22), BorderThickness = new Thickness(1), Child = body };
        surface.SetResourceReference(Border.BackgroundProperty, "AppBackdropBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderHoverBrush");
        surface.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        var prompt = new Window
        {
            Owner = _owner, Title = Str.T("Str.ResolutionTitle"), Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, Content = surface, Style = (Style)FindResource("WindowTypographyStyle")
        };
        prompt.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; RestoreClick(revert, new RoutedEventArgs()); } };
        prompt.Closing += (_, e) => { if (_trial) { e.Cancel = true; if (!_busy) RestoreClick(revert, new RoutedEventArgs()); } };
        prompt.ContentRendered += (_, _) => revert.Focus();
        _trialPrompt = prompt; prompt.Show(); revert.Focus();
    }
    private void CloseTrialPrompt()
    {
        var prompt = _trialPrompt; _trialPrompt = null; prompt?.Close();
    }
}
