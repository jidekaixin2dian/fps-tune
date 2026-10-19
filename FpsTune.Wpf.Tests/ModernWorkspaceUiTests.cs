using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ModernWorkspaceUiTests
{
    [Fact]
    public void Shell_search_routes_to_the_actual_section_preserves_selection_and_supports_keyboard_escape()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            SettingsService.Current.HotkeyEnabled = false;
            var window = new MainWindow();
            window.NavigateTo("opt");
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var page = (OptimizeView)((ContentControl)window.FindName("PageHost")).Content;
                var wanted = AppState.Items.First().Id;
                page.SelectForReview([wanted]);
                var expected = page.Items.Where(i => i.IsChecked).Select(i => i.Id).ToArray();
                var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.K && b.Modifiers == ModifierKeys.Control);
                Assert.Same(MainWindow.QuickAccessCommand, binding.Command);
                var searchButton = (Button)window.FindName("CommandSearchButton");
                Assert.IsType<System.Windows.Shapes.Path>(searchButton.Content);
                Assert.Equal(Str.T("Str.CommandSearch"), System.Windows.Automation.AutomationProperties.GetName(searchButton));
                searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var palette = (CommandPaletteView)((ContentControl)window.FindName("CommandHost")).Content;
                var query = (TextBox)palette.FindName("QueryBox"); var results = (ListBox)palette.FindName("ResultsList");
                Assert.Equal(Visibility.Visible, ((Grid)window.FindName("CommandOverlay")).Visibility);
                Assert.False(((ContentControl)window.FindName("PageHost")).IsEnabled);
                query.Text = wanted;
                Assert.Equal("item:" + wanted, ((WorkspaceCommand)results.SelectedItem).Id);
                PressKey(palette, window, Key.Enter);
                DlssFeedbackTests.PumpUntil(() => ((TextBox)page.FindName("SearchBox")).Text == wanted);
                Assert.Equal(expected, page.Items.Where(i => i.IsChecked).Select(i => i.Id).ToArray());
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("CommandOverlay")).Visibility);
                ((RoutedCommand)binding.Command).Execute(null, window); query.Text = "update";
                Assert.Equal("settings:5", ((WorkspaceCommand)results.SelectedItem).Id);
                PressKey(palette, window, Key.Enter);
                DlssFeedbackTests.PumpUntil(() => ((ContentControl)window.FindName("PageHost")).Content is SettingsView view
                    && ((RadioButton)view.FindName("SettingsNav5")).IsChecked == true);
                foreach (var language in new[] { LangService.ZhCn, LangService.EnUs })
                {
                    LangService.Apply(language);
                    foreach (var theme in new[] { "dark", "light" })
                    {
                        ThemeManager.SetMode(theme); window.Width = 1180;
                        window.OpenCommandPalette(); query.Text = "DLSS";
                        Assert.Contains(results.Items.Cast<WorkspaceCommand>(), r => r.Id == "display:dlss");
                        Render((FrameworkElement)window.FindName("ChromeRoot"), "search-" + language + "-" + theme);
                        query.Text = "no-such-feature-495921";
                        Assert.Equal(Visibility.Visible, ((TextBlock)palette.FindName("EmptyText")).Visibility);
                        PressKey(palette, window, Key.Escape);
                        Assert.True(((ContentControl)window.FindName("PageHost")).IsEnabled);
                    }
                }
                var search = new WorkspaceCommands();
                window.OpenCommandPalette();
                query.Text = "DLSS";
                var closeButton = (Button)palette.FindName("DismissButton");
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                closeButton.RaiseEvent(enter);
                Assert.False(enter.Handled);
                closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("CommandOverlay")).Visibility);
                var elapsed = new List<double>();
                for (var i = 0; i < 100; i++)
                {
                    var timer = Stopwatch.StartNew(); _ = search.Search(i % 2 == 0 ? "NVIDIA" : "update");
                    elapsed.Add(timer.Elapsed.TotalMilliseconds);
                }
                File.WriteAllText(Path.Combine(Output(), "search-latency.json"), JsonSerializer.Serialize(new {
                    queries = elapsed.Count, medianMs = elapsed.Order().ElementAt(50), maxMs = elapsed.Max(),
                    scope = "search index only; no game FPS or whole-app resource claim"
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Review_includes_hidden_items_cancel_writes_nothing_and_confirm_uses_immutable_ids_and_game()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            var page = new OptimizeView();
            var window = new Window { Width = 1180, Height = 790, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            DispatcherTimer? accept = null;
            DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
            try
            {
                var ids = AppState.Items.Take(2).Select(i => i.Id).ToArray();
                page.SelectForReview(ids);
                ((TextBox)page.FindName("SearchBox")).Text = ids[0];
                Assert.Equal(1, page.CaptureReviewPlan().HiddenCount);
                Assert.Contains(Str.T("Str.OptHiddenSelection", 1), ((TextBlock)page.FindName("SelectionSummary")).Text);
                var calls = 0;
                var completion = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                IReadOnlyList<string>? applied = null; string? appliedGame = null;
                page.ApplyItemsOverride = (selected, game) => { calls++; applied = selected; appliedGame = game; return completion.Task; };
                var consent = (CheckBox)page.FindName("ConsentCheck");
                var apply = (Button)page.FindName("ApplyButton");
                accept = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                var confirm = false; var observed = 0;
                var reviewCounts = new List<int>(); var hiddenStates = new List<Visibility>();
                accept.Tick += (_, _) =>
                {
                    var dialog = app.Windows.OfType<OptimizationReviewWindow>().SingleOrDefault(w => w.IsVisible);
                    if (dialog is null) return;
                    observed++;
                    reviewCounts.Add(((ListBox)dialog.FindName("ReviewItems")).Items.Count);
                    hiddenStates.Add(((TextBlock)dialog.FindName("HiddenSelectionText")).Visibility);
                    if (observed <= 2) Render((FrameworkElement)dialog.Content, "review-" + LangService.Current);
                    if (confirm) ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                };
                accept.Start();
                consent.IsChecked = true; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, observed); Assert.Equal(0, calls); Assert.True(apply.IsEnabled);
                Assert.Equal(2, reviewCounts[0]); Assert.Equal(Visibility.Visible, hiddenStates[0]);
                LangService.Apply(LangService.EnUs);
                confirm = true; AppState.GamePath = @"C:\WorkspaceTest\first.exe";
                consent.IsChecked = true; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, calls);
                Assert.Equal(2, reviewCounts[1]); Assert.Equal(Visibility.Visible, hiddenStates[1]);
                Assert.False(((FrameworkElement)page.FindName("PresetControlsCard")).IsEnabled);
                Assert.False(((Button)page.FindName("OneClickButton")).IsEnabled);
                Assert.False(((Button)page.FindName("RestoreButton")).IsEnabled);
                page.Items.First(i => i.Id == ids[1]).IsChecked = false;
                AppState.GamePath = @"C:\WorkspaceTest\second.exe";
                Assert.Equal(ids, applied); Assert.Equal(@"C:\WorkspaceTest\first.exe", appliedGame);
                completion.SetResult(new RunResult(0, "{\"results\":[],\"summary\":\"test completed\"}", ""));
                DlssFeedbackTests.PumpUntil(() => apply.IsEnabled);
                Assert.True(((FrameworkElement)page.FindName("PresetControlsCard")).IsEnabled);
                // A backend failure must restore controls without dropping retry choices.
                page.ApplyItemsOverride = (_, _) => { calls++; return Task.FromException<RunResult>(new IOException("controlled backend failure")); };
                consent.IsChecked = true;
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => apply.IsEnabled);
                Assert.Equal(2, calls); Assert.Equal(1, reviewCounts[2]); Assert.Equal(Visibility.Collapsed, hiddenStates[2]);
                Assert.True(((FrameworkElement)page.FindName("OptimizationItemsCard")).IsEnabled);
                accept.Stop();
                ((Button)page.FindName("ShowSelectedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(((TextBox)page.FindName("SearchBox")).Text);
                Assert.Single(page.ItemsView.Cast<object>());
                ((Button)page.FindName("ClearSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(page.Items.Where(i => i.IsChecked));
                Assert.Empty(page.ItemsView.Cast<object>());
                Assert.False(apply.IsEnabled);
                ((ComboBox)page.FindName("ScopeFilter")).SelectedIndex = 0;
                LangService.Apply(LangService.ZhCn);
                Render(page, "optimize-workspace");
            }
            finally { accept?.Stop(); DialogService.PrepareWindowForTest = null; window.Close(); }
        });
    }
    [Fact]
    public void Completed_operations_refresh_readback_keep_context_and_do_not_invent_success_when_refresh_fails()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            foreach (var scenario in new[] { "success", "partial", "refresh-failed" })
            {
                AppState.Items = ItemCatalog.All.Where(d => d.Id is "gpu-pref" or "dvr-off" or "game-mode").Select(d =>
                    new OptimizationItem(d.Id, d.DisplayName, d.DisplayDescription, d.DisplaySideEffect, false, false, false, "before", false, d.Group)).ToList();
                var before = AppState.Items;
                var page = new OptimizeView();
                var surface = new Border { Child = page };
                surface.SetResourceReference(Border.BackgroundProperty, "AppBackdropBrush");
                var window = new Window { Width = 1180, Height = 720, Content = surface };
                app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
                var readback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var refreshes = 0;
                page.RefreshDetectionOverride = async () =>
                {
                    refreshes++;
                    var ok = await readback.Task;
                    if (ok) AppState.Items = before.Select(i => i.Id == "game-mode" ? i : i with
                        { Optimized = i.Id == "gpu-pref" || scenario == "success", Current = "read back: " + i.Id }).ToList();
                    return ok;
                };
                page.ApplyItemsOverride = (_, _) => Task.FromResult(new RunResult(scenario == "partial" ? 1 : 0,
                    JsonSerializer.Serialize(new { results = new[] { new { id = "gpu-pref", ok = true, message = "receipt-" + scenario } } }), ""));
                DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
                var accept = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                accept.Tick += (_, _) =>
                {
                    if (app.Windows.OfType<OptimizationReviewWindow>().FirstOrDefault(w => w.IsVisible) is { } review)
                        ((Button)review.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                try
                {
                    page.SelectForReview(["gpu-pref", "dvr-off"]);
                    var focus = ((OptimizationItemViewModel)((ListBox)page.FindName("ItemList")).SelectedItem).Id;
                    var consent = (CheckBox)page.FindName("ConsentCheck"); consent.IsChecked = true;
                    accept.Start(); ((Button)page.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(1, refreshes);
                    Assert.False(((FrameworkElement)page.FindName("OptimizationItemsCard")).IsEnabled);
                    Assert.All(page.Items, i => Assert.False(i.Optimized));
                    readback.SetResult(scenario != "refresh-failed");
                    DlssFeedbackTests.PumpUntil(() => ((FrameworkElement)page.FindName("OptimizationItemsCard")).IsEnabled);
                    Assert.Equal(scenario != "refresh-failed", page.Items.Single(i => i.Id == "gpu-pref").Optimized);
                    Assert.Equal(scenario == "success", page.Items.Single(i => i.Id == "dvr-off").Optimized);
                    Assert.Equal(2, page.Items.Count(i => i.IsChecked)); Assert.Equal(2, page.ItemsView.Cast<object>().Count());
                    Assert.Equal(focus, ((OptimizationItemViewModel)((ListBox)page.FindName("ItemList")).SelectedItem).Id);
                    Assert.False(consent.IsChecked);
                    Assert.Equal(Str.T("Str.OptSelectionSummary", 2, page.Items.Count(i => i.IsChecked && i.Optimized), 0, 0),
                        ((TextBlock)page.FindName("SelectionSummary")).Text);
                    var doc = (System.Windows.Documents.FlowDocument)page.FindName("OutputDoc");
                    var text = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd).Text;
                    Assert.Contains("receipt-" + scenario, text);
                    Assert.Equal(scenario == "refresh-failed", text.Contains(Str.T("Str.OptRefreshFailed", Str.T("Str.DetectIncompleteRetry"))));
                    Assert.Equal(scenario != "success", DeltaPreparation.PendingItems(AppState.Items, true).Contains("dvr-off"));
                    foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(surface, "status-refresh-" + scenario + "-" + theme); }
                }
                finally { accept.Stop(); window.Close(); DialogService.PrepareWindowForTest = null; }
            }
        });
    }

    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static void RunUi(Action<Application, string> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Output(), "test-data", Guid.NewGuid().ToString("N"));
            var oldItems = AppState.Items; var oldGame = AppState.GamePath;
            var oldLowSpec = UiPerformance.LowSpec;
            Application? app = null;
            try
            {
                UserDataPaths.RootOverride = root;
                UiPerformance.LowSpec = true;
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = DlssFeedbackTests.LoadResources() };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                LangService.Apply(LangService.ZhCn);
                AppState.Items = ItemCatalog.All.Where(d => d.Available).Take(4).Select(d => new OptimizationItem(
                    d.Id, d.DisplayName, d.DisplayDescription, d.DisplaySideEffect, false, false, false, "test original", false, d.Group)).ToList();
                AppState.GamePath = @"C:\WorkspaceTest\game.exe";
                test(app, root);
            }
            catch (Exception ex) { error = ex; }
            finally { app?.Shutdown(); UserDataPaths.RootOverride = null; AppState.Items = oldItems; AppState.GamePath = oldGame; UiPerformance.LowSpec = oldLowSpec; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Modern workspace UI test did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void PressKey(UIElement element, Window window, Key key) => element.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static string Output()
    {
        var path = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "modern-workspace", "ui"); Directory.CreateDirectory(path); return path;
    }
    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(drawing);
        using var stream = File.Create(Path.Combine(Output(), name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
