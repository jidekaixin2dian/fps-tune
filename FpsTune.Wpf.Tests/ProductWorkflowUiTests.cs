using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ProductWorkflowUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "product036-20261030");

    [Fact]
    public void Library_mutations_preserve_unknown_data_and_recovery_files_refuse_stale_locked_and_corrupt_sources()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ProductWorkflowUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((_, root) =>
        {
            var a = StateStore.AddGame(AppState.GamePath!); var b = StateStore.AddGame(Path.Combine(root, "other.exe"));
            var file = StateStore.GamesFilePath;
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsArray(); json[0]!["future"] = new JsonObject { ["keep"] = true };
            File.WriteAllText(file, json.ToJsonString());
            var original = File.ReadAllText(file);
            Assert.Equal(a.Id, StateStore.AddGame(a.ExePath.ToUpperInvariant()).Id); Assert.Equal(original, File.ReadAllText(file));
            StateStore.SaveDetectForGame(a.Id, new JsonObject { ["sentinel"] = "keep" });
            SessionTemplateStore.Save(a.ExePath, new SessionConditions(Scene: "Keep template"));
            StateStore.SaveGamePath(a.ExePath);
            GameLibraryService.Rename(a, "训练游戏"); var renamed = GameLibraryService.Read().Single(p => p.Id == a.Id);
            Assert.Equal(a.ExePath, renamed.ExePath); Assert.Equal(a.AddedAt, renamed.AddedAt);
            Assert.True(JsonNode.Parse(File.ReadAllText(file))![0]!["future"]!["keep"]!.GetValue<bool>());
            Assert.Equal("训练游戏", SessionConditionRules.CaptureGame(a.ExePath, null).GameName);
            var after = File.ReadAllText(file);
            Assert.Throws<IOException>(() => GameLibraryService.Rename(a, "Stale")); Assert.Equal(after, File.ReadAllText(file));
            using (File.Open(Path.ChangeExtension(file, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => GameLibraryService.Remove(renamed));
            Assert.Equal(after, File.ReadAllText(file));
            var removed = GameLibraryService.Remove(renamed);
            Assert.Equal(b.Id, Assert.Single(GameLibraryService.Read()).Id);
            Assert.Equal("Keep template", SessionTemplateStore.Load(a.ExePath)!.Scene);
            Assert.Equal("keep", StateStore.LoadDetectForGame(a.Id)!["sentinel"]!.GetValue<string>());
            GameLibraryService.UndoRemove(removed);
            Assert.True(JsonNode.Parse(File.ReadAllText(file))!.AsArray().Single(n => n!["Id"]!.GetValue<string>() == a.Id)!["future"]!["keep"]!.GetValue<bool>());
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "game-library-backups")));
            File.WriteAllText(file, "{\"futureSchema\":2}");
            Assert.Throws<InvalidDataException>(() => StateStore.AddGame(Path.Combine(root, "new.exe")));
            Assert.Equal("{\"futureSchema\":2}", File.ReadAllText(file));
            Assert.Equal("fixture; never executed", File.ReadAllText(a.ExePath));
        });
    }

    [Fact]
    public void Game_preparation_scope_survives_reload_failed_saves_and_target_changes_with_archived_override_recovery()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ProductWorkflowUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var first = AppState.GamePath!; var second = Path.Combine(root, "second.exe");
            var common = new GamePreparationOptions { Configured = true };
            PreparationPlanStore.Save(null, common);
            var special = common with { AllStandby = true, ApplyVibrance = true, VibrancePercent = 72, DisplayIdentity = "B" };
            PreparationPlanStore.Save(first, special); SettingsService.Load();
            Assert.Equal(special, PreparationPlanStore.Resolve(first).Options); Assert.True(PreparationPlanStore.Resolve(first).IsGameSpecific);
            Assert.Equal(common, PreparationPlanStore.Resolve(second).Options); Assert.False(PreparationPlanStore.Resolve(second).IsGameSpecific);
            var file = PreparationPlanStore.FileFor(first); var bytes = File.ReadAllBytes(file);
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => PreparationPlanStore.Save(first, common));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal(bytes, File.ReadAllBytes(file));
            using (File.Open(Path.Combine(root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => PreparationPlanStore.Save(null, common with { AllStandby = true }));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal(common, SettingsService.Current.Preparation);
            DisplayTopology.Override = () => [MultiDisplayColorTests.Screen("B", "DISPLAY2", 1, "0002", true)];
            var owner = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = owner;
            DlssFeedbackTests.PrepareBackgroundWindow(owner); owner.Show();
            try
            {
                var dialog = new GamePreparationWindow { Owner = owner }; DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
                {
                    var save = (Button)dialog.FindName("SaveButton"); DlssFeedbackTests.PumpUntil(() => save.IsEnabled);
                    Assert.True(((RadioButton)dialog.FindName("GameScope")).IsChecked);
                    foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render((FrameworkElement)dialog.Content, "preparation-scope-" + theme); }
                    AppState.GamePath = second; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Str.T("Str.PlanGameChanged"), ((TextBlock)dialog.FindName("StatusText")).Text);
                    Assert.False(File.Exists(PreparationPlanStore.FileFor(second)));
                    AppState.GamePath = first;
                    ((RadioButton)dialog.FindName("DefaultScope")).IsChecked = true;
                    ((CheckBox)dialog.FindName("VibranceCheck")).IsChecked = false;
                    ((ComboBox)dialog.FindName("MemoryMode")).SelectedIndex = 0;
                    ((Button)dialog.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Assert.True(dialog.ShowDialog()); Assert.True(dialog.RunNow);
                Assert.False(dialog.SavedOptions!.ApplyVibrance); Assert.True(PreparationPlanStore.Resolve(first).Options.ApplyVibrance);
                Assert.False(SettingsService.Current.Preparation.ApplyVibrance);
                File.WriteAllText(file, "{\"SchemaVersion\":99}");
                Assert.Throws<InvalidDataException>(() => PreparationPlanStore.Resolve(first));
                Assert.Throws<InvalidDataException>(() => PreparationPlanStore.Save(first, common));
                PreparationPlanStore.UseDefault(first);
                Assert.False(PreparationPlanStore.Resolve(first).IsGameSpecific);
                Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(file)!), p => p.Contains(".disabled-") && File.ReadAllText(p).Contains("99"));
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void Comparison_reports_share_units_and_directions_keep_missing_and_zero_baselines_honest_and_encode_private_input()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ProductWorkflowUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((_, _) =>
        {
            var a = SessionConditionTests.Example("演示 · 基准");
            var b = SessionConditionTests.Example("演示 · 对照") with { Fps = new(4000, 144, 100, 10, 0) };
            var comparison = SessionComparison.Create(a, b, SessionStatistics.Summarize(a), SessionStatistics.Summarize(b));
            Assert.Empty(comparison.Issues); Assert.All(comparison.Highlights, h => Assert.Equal("good", h.Trend));
            Assert.Equal("+24 FPS", comparison.Highlights[0].Delta); Assert.Equal("+20%", comparison.Highlights[0].Relative);
            Assert.Equal("-2 ms", comparison.Highlights[2].Delta);
            Assert.Equal(120, a.Fps!.AvgFps); // Source data is not rewritten.
            Directory.CreateDirectory(Output); SessionComparisonReport.Export(comparison, Path.Combine(Output, "comparison-demo.html"));
            var zero = a with { Fps = a.Fps with { AvgFps = 0 } };
            var fromZero = SessionComparison.Create(zero, b, SessionStatistics.Summarize(zero), SessionStatistics.Summarize(b));
            Assert.Equal(Str.T("Str.ComparisonZeroBase"), fromZero.Highlights[0].Relative);
            var changed = b with { Conditions = b.Conditions! with { Width = 2560, Scene = "不同场景" }, Fps = null };
            var unknown = SessionComparison.Create(a, changed, SessionStatistics.Summarize(a), SessionStatistics.Summarize(changed));
            Assert.NotEmpty(unknown.Issues); Assert.All(unknown.Highlights, h => Assert.Equal("neutral", h.Trend));
            Assert.Equal(Str.T("Str.Unavailable"), unknown.Highlights[0].BVal);
            SessionComparisonReport.Export(unknown, Path.Combine(Output, "comparison-conditions-demo.html"));
            var privateName = a with { Name = "<script>alert(1)</script> C:\\Users\\PrivateName\\secret.exe", Conditions = a.Conditions! with { Scene = "<img src=x onerror=alert(1)>" } };
            var html = SessionComparisonReport.BuildHtml(SessionComparison.Create(privateName, b, SessionStatistics.Summarize(a), SessionStatistics.Summarize(b)));
            Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<img src", html); Assert.DoesNotContain("PrivateName", html);
            Assert.Contains("&lt;script&gt;", html); Assert.Contains("Content-Security-Policy", html);
            Assert.DoesNotContain("!Str.", html); Assert.DoesNotContain("NaN", html); Assert.DoesNotContain("Infinity", html);
            Assert.Throws<ArgumentException>(() => SessionComparison.Create(a, a, new(), new()));
        });
    }

    [Fact]
    public void Game_library_and_comparison_actual_workflows_keep_targets_reachable_and_render_themes_at_minimum_size()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ProductWorkflowUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var first = StateStore.AddGame(AppState.GamePath!); var otherPath = Path.Combine(root, "OtherGame.exe"); File.WriteAllText(otherPath, "never executed");
            var second = StateStore.AddGame(otherPath);
            var a = SessionConditionTests.Example("演示 · 基准");
            var b = SessionConditionTests.Example("演示 · 对照") with { StartedAt = a.StartedAt.AddMinutes(5), EndedAt = a.EndedAt!.Value.AddMinutes(5),
                Samples = a.Samples.Select(s => s with { T = s.T.AddMinutes(5), CpuPct = 25 }).ToArray(), Fps = new(4000, 144, 100, 10, 0) };
            foreach (var entry in PerformanceSessionStore.LoadHistory()) PerformanceSessionStore.Delete(entry.Header.Id);
            PerformanceSessionStore.Save(a); PerformanceSessionStore.Save(b);
            var main = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show(); main.NavigateTo("games");
            var library = (GameLibraryView)((ContentControl)main.FindName("PageHost")).Content;
            var games = (ListBox)library.FindName("GameList");
            void Click(FrameworkElement page, string name) => ((Button)page.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            static StateStore.GameProfile Profile(object row) => (StateStore.GameProfile)row.GetType().GetProperty("Profile")!.GetValue(row)!;
            try
            {
                DlssFeedbackTests.PumpUntil(() => games.Items.Count == 2 && ((StackPanel)library.FindName("DetailPanel")).IsEnabled);
                games.SelectedItem = games.Items.Cast<object>().Single(r => Profile(r).Id == first.Id);
                ((TextBox)library.FindName("NameBox")).Text = "训练游戏"; Click(library, "RenameButton");
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)library.FindName("GameNameText")).Text == "训练游戏" && ((StackPanel)library.FindName("DetailPanel")).IsEnabled);
                Assert.Equal("训练游戏", GameContextService.DisplayNameFor(first.ExePath));
                foreach (var mode in new[] { "studio", "console" })
                foreach (var theme in new[] { "light", "dark" })
                { main.SetDisplayMode(mode); ThemeManager.SetMode(theme); main.UpdateLayout(); Render((FrameworkElement)main.FindName("ChromeRoot"), "library-" + mode + "-" + theme); }
                ((TextBox)library.FindName("SearchBox")).Text = "nothing matches";
                Assert.Empty(games.Items); Assert.False(((StackPanel)library.FindName("DetailPanel")).IsEnabled);
                ((TextBox)library.FindName("SearchBox")).Clear();
                games.SelectedItem = games.Items.Cast<object>().Single(r => Profile(r).Id == first.Id);
                Click(library, "AutoRecordButton");
                var page = (SessionView)((ContentControl)main.FindName("PageHost")).Content;
                Assert.True(((Expander)page.FindName("AutoRecordOptions")).IsExpanded);
                var history = (ListBox)page.FindName("HistoryList"); DlssFeedbackTests.PumpUntil(() => history.Items.Count == 2);
                foreach (var row in history.Items) history.SelectedItems.Add(row);
                Assert.True(((Button)page.FindName("CompareSelectedButton")).IsEnabled); Click(page, "CompareSelectedButton");
                Assert.Equal(3, ((ItemsControl)page.FindName("CompareHighlights")).Items.Count);
                var comparisonPath = Path.Combine(Output, "comparison-ui-demo.html"); page.ExportComparisonTo(comparisonPath);
                Assert.Contains("+24 FPS", File.ReadAllText(comparisonPath));
                foreach (var mode in new[] { "studio", "console" })
                foreach (var theme in new[] { "light", "dark" })
                {
                    main.SetDisplayMode(mode); ThemeManager.SetMode(theme); main.UpdateLayout();
                    page.FocusComparison();
                    var settled = false; page.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => settled = true));
                    DlssFeedbackTests.PumpUntil(() => settled); main.UpdateLayout();
                    var card = (Border)page.FindName("ComparisonCard"); var top = card.TranslatePoint(new Point(0, 0), main).Y;
                    Assert.True(top >= 58 && top + card.ActualHeight <= main.ActualHeight - 8,
                        $"The collapsed comparison must fit in the viewport: top={top}, height={card.ActualHeight}, window={main.ActualHeight}");
                    Render((FrameworkElement)page.FindName("ComparisonCard"), "comparison-" + mode + "-" + theme);
                    Render((FrameworkElement)main.FindName("ChromeRoot"), "comparison-viewport-" + mode + "-" + theme);
                }
                Click(page, "SwapComparisonButton"); page.ExportComparisonTo(Path.Combine(Output, "comparison-swapped-demo.html"));
                Assert.Contains("-24 FPS", File.ReadAllText(Path.Combine(Output, "comparison-swapped-demo.html")));
                ((TextBox)page.FindName("HistorySearchBox")).Text = a.Name;
                Assert.False(((Button)page.FindName("ExportComparisonButton")).IsEnabled);
                Assert.Throws<InvalidOperationException>(() => page.ExportComparisonTo(Path.Combine(Output, "must-not-export.html")));
                main.NavigateTo("games"); DlssFeedbackTests.PumpUntil(() => games.Items.Count == 2 && ((StackPanel)library.FindName("DetailPanel")).IsEnabled);
                games.SelectedItem = games.Items.Cast<object>().Single(r => Profile(r).Id == second.Id);
                SettingsService.Current.AutoRecordGames = [otherPath]; Click(library, "RemoveButton");
                Assert.Equal(Str.T("Str.LibraryLinkedAutomation"), ((TextBlock)library.FindName("StatusText")).Text);
                SettingsService.Current.AutoRecordGames = [];
                DialogService.PrepareWindowForTest = dialog => { DlssFeedbackTests.PrepareBackgroundWindow(dialog); dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => dialog.DialogResult = true); };
                Click(library, "RemoveButton"); DlssFeedbackTests.PumpUntil(() => games.Items.Count == 1);
                Assert.True(File.Exists(otherPath)); Click(library, "UndoRemoveButton"); DlssFeedbackTests.PumpUntil(() => games.Items.Count == 2);
                var before = StateStore.FindGameByPath(otherPath)!; GameContextService.IsBusyProvider = () => true;
                ((TextBox)library.FindName("NameBox")).Text = "Busy rename"; Click(library, "RenameButton");
                Assert.Equal(before, StateStore.FindGameByPath(otherPath));
                Assert.Equal(Str.T("Str.LibraryBusy"), ((TextBlock)library.FindName("StatusText")).Text);
                Assert.False(main.IsActive);
            }
            finally { GameContextService.IsBusyProvider = null; main.Close(); }
        });
    }

    private static void Render(FrameworkElement element, string name)
    {
        // Capture the actual window root, then crop. Rendering a scrolled child directly
        // includes its ancestor offset/clip and can otherwise produce an empty bitmap.
        var owner = Window.GetWindow(element); var root = owner?.Content as FrameworkElement ?? element;
        root.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle(owner?.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(background); bitmap.Render(root);
        var point = ReferenceEquals(element, root) ? new Point() : element.TranslatePoint(new Point(), root);
        var x = Math.Clamp((int)Math.Floor(point.X * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(point.Y * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1);
        var cropped = new CroppedBitmap(bitmap, new Int32Rect(x, y, Math.Min(bitmap.PixelWidth - x, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)), Math.Min(bitmap.PixelHeight - y, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY))));
        var pixels = new byte[cropped.PixelWidth * cropped.PixelHeight * 4]; cropped.CopyPixels(pixels, cropped.PixelWidth * 4, 0);
        Assert.True(pixels.Chunk(4).Select(p => BitConverter.ToInt32(p)).Distinct().Take(20).Count() > 10, "Rendered page must contain actual control/text pixels.");
        var folder = Path.Combine(Output, "ui"); Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name + ".png")); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped)); encoder.Save(stream);
    }
}
