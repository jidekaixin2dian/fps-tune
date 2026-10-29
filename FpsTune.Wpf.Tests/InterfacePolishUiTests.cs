using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class InterfacePolishUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Shared_focus_catalog_glyphs_compact_header_and_wallpaper_preview_render_and_keep_actions_reachable()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(InterfacePolishUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var main = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show(); main.NavigateTo("opt"); main.UpdateLayout();
            try
            {
                var page = (FrameworkElement)((ContentControl)main.FindName("PageHost")).Content;
                var list = (ListBox)page.FindName("ItemList");
                list.SelectedIndex = 0; main.UpdateLayout();
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.Same(app.FindResource("KeyboardFocusStyle"), row.FocusVisualStyle);
                var focus = new Control { Style = (Style)app.FindResource(SystemParameters.FocusVisualStyleKey), Width = 500, Height = 130 };
                focus.ApplyTemplate(); focus.Measure(new Size(500, 130)); focus.Arrange(new Rect(0, 0, 500, 130));
                var border = ReadabilityAndFramesUiTests.Descendants(focus).OfType<Border>().Single();
                Assert.Equal(new Thickness(1), border.BorderThickness);
                Assert.Empty(ReadabilityAndFramesUiTests.Descendants(focus).OfType<System.Windows.Shapes.Rectangle>());
                Render(focus, "keyboard-solid-focus");
                var font = new FontFamily(new Uri("pack://application:,,,/"), "./FpsTune;component/Assets/Fonts/#FpsTune Text");
                var catalog = string.Join("", FpsTune.Wpf.Core.ItemCatalog.All.Select(i => i.DisplayName + i.DisplayDescription + i.DisplaySideEffect));
                foreach (var weight in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold })
                {
                    Assert.True(new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var glyph));
                    Assert.All(catalog.Where(c => c is >= '\u4e00' and <= '\u9fff').Distinct(), c => Assert.True(glyph.CharacterToGlyphMap.ContainsKey(c), $"Missing catalog glyph U+{(int)c:X4}"));
                }
                var title = ReadabilityAndFramesUiTests.Descendants(page).OfType<TextBlock>().FirstOrDefault(t => t.Text == "解除多媒体网络限流");
                if (title is null)
                {
                    ((TextBox)page.FindName("SearchBox")).Text = "多媒体"; main.UpdateLayout();
                    title = ReadabilityAndFramesUiTests.Descendants(page).OfType<TextBlock>().Single(t => t.Text == "解除多媒体网络限流");
                }
                title.BringIntoView(); main.UpdateLayout(); Render(title, "catalog-title");
                var runs = Glyphs(VisualTreeHelper.GetDrawing(title)).Where(g => g.GlyphRun.Characters?.Any(c => c is >= '\u4e00' and <= '\u9fff') == true).ToArray();
                Assert.NotEmpty(runs); Assert.All(runs, g => Assert.Contains("FpsTune Text", g.GlyphRun.GlyphTypeface.FamilyNames.Values));
                var menu = ((Button)main.FindName("MoreButton")).ContextMenu;
                Assert.Equal(5, menu.Items.Count);
                Assert.NotNull(main.FindName("GuideMenuItem")); Assert.NotNull(main.FindName("FeedbackMenuItem"));
                Assert.NotEmpty(((TextBlock)main.FindName("AdminStatusText")).Text);
                ((MenuItem)main.FindName("GuideMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(Visibility.Visible, ((ScrollViewer)main.FindName("NotificationArea")).Visibility);
                main.NavigateTo("settings"); main.UpdateLayout();
                var settings = (FrameworkElement)((ContentControl)main.FindName("PageHost")).Content;
                ((RadioButton)settings.FindName("SettingsNav4")).IsChecked = true; main.UpdateLayout();
                Assert.Equal(Visibility.Visible, ((StackPanel)settings.FindName("WallpaperPreviewPlaceholder")).Visibility);
                Assert.True(((Border)settings.FindName("WallpaperPreviewFrame")).ActualWidth > ((Border)settings.FindName("WallpaperPreviewFrame")).ActualHeight);
                foreach (var mode in new[] { "studio", "console" })
                foreach (var theme in new[] { "light", "dark" })
                {
                    main.SetDisplayMode(mode); ThemeManager.SetMode(theme); main.UpdateLayout();
                    Render((FrameworkElement)main.FindName("TitleBar"), "titlebar-" + mode + "-" + theme);
                    Render((FrameworkElement)settings.FindName("WallpaperCard"), "wallpaper-empty-" + mode + "-" + theme);
                    menu.ApplyTemplate(); menu.Measure(new Size(220, 400)); menu.Arrange(new Rect(0, 0, 220, menu.DesiredSize.Height));
                    Render(menu, "more-menu-" + mode + "-" + theme);
                }
                ((MenuItem)main.FindName("OverviewModeButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("studio", SettingsService.Current.OverviewMode);
                // An actual local image still fills the preview, and opacity retains its existing behavior.
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 320, 180));
                var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var path = Path.Combine(root, "wallpaper-fixture.png");
                using (var stream = File.Create(path)) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(stream); }
                WallpaperService.Import(path);
                main.NavigateTo("session"); main.NavigateTo("settings"); main.UpdateLayout();
                settings = (FrameworkElement)((ContentControl)main.FindName("PageHost")).Content;
                ((RadioButton)settings.FindName("SettingsNav4")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => ((StackPanel)settings.FindName("WallpaperPreviewPlaceholder")).Visibility == Visibility.Collapsed);
                ((Slider)settings.FindName("WallpaperOpacitySlider")).Value = .25;
                Assert.Equal(.25, ((Border)settings.FindName("WallpaperPreviewImage")).Opacity);
                main.UpdateLayout(); Render((FrameworkElement)settings.FindName("WallpaperCard"), "wallpaper-image");
            }
            finally { main.Close(); }
        });
    }

    [Fact]
    public void Dropdown_uses_native_fade_in_both_directions_reopens_cleanly_and_disables_motion_in_low_spec()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(InterfacePolishUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var combo = new ComboBox { Style = (Style)app.FindResource("SelectComboBoxStyle"), Width = 300 };
            combo.Items.Add("三角洲行动"); combo.Items.Add("VALORANT"); combo.SelectedIndex = 0;
            var window = new Window { Width = 500, Height = 300, Content = combo }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            var templatePopup = (Popup)combo.Template.FindName("PART_Popup", combo);
            UiPerformance.LowSpec = false; ThemeManager.SetMode("light");
            Assert.Equal(SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None, templatePopup.PopupAnimation);
            // Zero-opacity, non-interactive native popup: exercises WPF's actual animation clocks
            // without displaying test UI or taking mouse capture on the user's desktop.
            var child = new Border { Width = 20, Height = 20, Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
            var popup = new Popup { AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Child = child,
                PlacementTarget = combo, StaysOpen = true, Focusable = false, IsHitTestVisible = false };
            var closed = 0; popup.Closed += (_, _) => closed++;
            try
            {
                popup.IsOpen = true;
                DlssFeedbackTests.PumpUntil(() => PresentationSource.FromVisual(child)?.RootVisual is UIElement);
                var root = (UIElement)PresentationSource.FromVisual(child)!.RootVisual;
                DlssFeedbackTests.PumpUntil(() => root.Opacity is > 0 and < 1);
                DlssFeedbackTests.PumpUntil(() => root.Opacity >= .999);
                popup.IsOpen = false;
                DlssFeedbackTests.PumpUntil(() => root.Opacity is > 0 and < 1);
                Assert.Equal(0, closed); Assert.Null(System.Windows.Input.Mouse.Captured);
                popup.IsOpen = true; DlssFeedbackTests.PumpUntil(() => root.Opacity >= .999);
                Assert.True(popup.IsOpen); Assert.Equal(0, closed);
                popup.IsOpen = false; DlssFeedbackTests.PumpUntil(() => closed == 1);
                UiPerformance.LowSpec = true; ThemeManager.SetMode("light");
                Assert.Equal(PopupAnimation.None, templatePopup.PopupAnimation);
                Assert.False(window.IsActive);
            }
            finally { popup.PopupAnimation = PopupAnimation.None; popup.IsOpen = false; window.Close(); }
        });
    }

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var run in Glyphs(child)) yield return run;
    }
    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var brush = new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds };
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, bounds);
        bitmap.Render(visual);
        var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "session-workspace-20261029", "polish-ui"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
