using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using FpsTune.Wpf.Views.Controls;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class DesignSystemUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "design036", "ui");

    [Fact]
    public void Nested_surfaces_keep_one_outline_and_concentric_radii_across_theme_and_reload()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(DesignSystemUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var inner = new Border { BorderBrush = Brushes.Red, BorderThickness = new Thickness(1), Background = Brushes.Transparent, CornerRadius = new CornerRadius(12) };
            var outer = new Border { Style = (Style)app.FindResource("CardStyle"), Padding = new Thickness(8), CornerRadius = new CornerRadius(24), Child = inner };
            var window = new Window { Width = 500, Height = 240, Content = outer };
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            try
            {
                Assert.Equal(new Thickness(1), outer.BorderThickness); Assert.Equal(new Thickness(0), inner.BorderThickness);
                // Border stroke also contributes to the distance between the two curves.
                Assert.Equal(new CornerRadius(15), inner.CornerRadius);
                outer.CornerRadius = new CornerRadius(28); window.UpdateLayout(); Assert.Equal(new CornerRadius(19), inner.CornerRadius);
                outer.Child = null; outer.Child = inner; Pump(30); window.UpdateLayout();
                Assert.Equal(new Thickness(0), inner.BorderThickness); Assert.Equal(new CornerRadius(19), inner.CornerRadius);
                foreach (var mode in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(mode); var surface = ((SolidColorBrush)app.FindResource("SurfaceBrush")).Color;
                    var edge = ((SolidColorBrush)app.FindResource("BorderBrush")).Color;
                    Assert.All(new[] { Math.Abs(surface.R-edge.R), Math.Abs(surface.G-edge.G), Math.Abs(surface.B-edge.B) }, delta => Assert.InRange(delta, 2, 8));
                    var action = new Button { Style = (Style)app.FindResource("SecondaryButtonStyle"), Content = "Action" };
                    inner.Child = action; window.UpdateLayout(); action.ApplyTemplate();
                    Assert.Equal(new Thickness(0), ((Border)action.Template.FindName("Root", action)).BorderThickness);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Real_pages_use_responsive_weights_tabular_digits_and_suffix_preserving_labels()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(DesignSystemUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var main = new MainWindow { Width = mainWidth, Height = 720 }; app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show();
            try
            {
                foreach (var theme in new[] { "light", "dark" })
                {
                    SettingsService.Current.ThemeMode = theme; ThemeManager.SetMode(theme);
                    foreach (var layout in new[] { "studio" })
                    {
                        foreach (var route in new[] { "home", "games", "settings", "session" })
                        {
                            main.NavigateTo(route); main.UpdateLayout();
                            Render((FrameworkElement)main.Content, route + "-" + layout + "-" + theme);
                        }
                    }
                    var title = new TextBlock { Style = (Style)app.FindResource("CardTitleStyle"), Text = "Metrics" };
                    var number = new TextBlock { Style = (Style)app.FindResource("StatValueStyle") };
                    var label = new FileNameLabel { Text = @"C:\Games\A very long executable name and directory\游戏启动器.exe", Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
                    var stack = new StackPanel { Margin = new Thickness(16) }; stack.Children.Add(title); stack.Children.Add(number); stack.Children.Add(label);
                    var sample = new Window { Width = 350, Height = 200, Content = stack };
                    DlssFeedbackTests.PrepareBackgroundWindow(sample); sample.Show(); sample.UpdateLayout();
                    try
                    {
                        Assert.Equal(theme == "light" ? FontWeights.Medium : FontWeights.Normal, title.FontWeight);
                        Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(number));
                        number.Text = "1111"; number.Measure(new Size(double.PositiveInfinity, 60)); var width = number.DesiredSize.Width;
                        number.Text = "8888"; number.Measure(new Size(double.PositiveInfinity, 60)); Assert.InRange(Math.Abs(width-number.DesiredSize.Width), 0, 0.1);
                        foreach (var name in new[] { "a very long filename executable.exe", "一个非常长的颜色配置文件.icc", "screen image with long name.png", "backup archive.tar.gz", "README", ".gitignore" })
                        {
                            label.Text = name; sample.UpdateLayout();
                            var extension = (TextBlock)label.Template.FindName("ExtensionText", label);
                            Assert.Equal(label.Extension, extension.Text); Assert.True(extension.ActualWidth <= label.ActualWidth);
                            Assert.Equal(name, label.ToolTip);
                        }
                        label.Text = "a very long color profile name with more characters.icc"; sample.UpdateLayout(); Render(stack, "file-and-digits-" + theme);
                    }
                    finally { sample.Close(); }
                }
            }
            finally { main.Close(); }
        });
    }
    private const double mainWidth = 1180;

    [Fact]
    public void Grid_disclosure_reverses_smoothly_and_feedback_character_respects_reduced_motion()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(DesignSystemUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            UiMotion.EnabledOverride = true;
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
            for (var i = 0; i < 9; i++) grid.Children.Add(new Border { Height = 50, Margin = new Thickness(4), Background = Brushes.SlateGray });
            var disclosure = new DisclosurePanel { Child = grid };
            var button = new Button { Content = "Save", Style = (Style)app.FindResource("SecondaryButtonStyle") };
            var stack = new StackPanel(); stack.Children.Add(button); stack.Children.Add(disclosure);
            var window = new Window { Width = 500, Height = 360, Content = stack };
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            try
            {
                disclosure.IsExpanded = true; Pump(85); window.UpdateLayout();
                Assert.InRange(disclosure.Reveal, 0.01, 0.99); var partial = disclosure.ActualHeight;
                disclosure.IsExpanded = false; Assert.False(disclosure.IsEnabled); Pump(55);
                Assert.InRange(disclosure.Reveal, 0, 0.99); disclosure.IsExpanded = true;
                Pump(260); window.UpdateLayout(); Assert.Equal(1, disclosure.Reveal); Assert.True(disclosure.ActualHeight > partial);
                Render(stack, "grid-expanded");
                UiMotion.SetCharacter(button, FeedbackCharacter.Spring); UiMotion.Press(button, true); Pump(100); UiMotion.Press(button, false); Pump(100);
                var transform = Assert.IsType<ScaleTransform>(((Border)button.Template.FindName("Root", button)).RenderTransform);
                Assert.True(transform.ScaleX > 1); Pump(200); Assert.Equal(1, transform.ScaleX);
                UiMotion.SetCharacter(button, FeedbackCharacter.Immediate); UiMotion.Press(button, true); Assert.Equal(1, transform.ScaleX);
                UiMotion.EnabledOverride = false; UiMotion.SetCharacter(button, FeedbackCharacter.Spring); UiMotion.Press(button, true); Assert.Equal(1, transform.ScaleX);
                disclosure.IsExpanded = false; Assert.Equal(0, disclosure.Reveal); Assert.Equal(Visibility.Collapsed, disclosure.Visibility);
                Assert.Equal(9, grid.Children.Count);
            }
            finally { window.Close(); UiMotion.EnabledOverride = null; }
        });
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout(); Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        var dpi = VisualTreeHelper.GetDpi(element); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight*dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, new Rect(0,0,element.ActualWidth,element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0,0,element.ActualWidth,element.ActualHeight) }, null, new Rect(0,0,element.ActualWidth,element.ActualHeight));
        }
        bitmap.Render(visual); Directory.CreateDirectory(Output); using var stream = File.Create(Path.Combine(Output,name+".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
