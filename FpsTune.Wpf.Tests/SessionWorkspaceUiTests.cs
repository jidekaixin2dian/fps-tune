using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class SessionWorkspaceUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static PerformanceSessionService HardwareOnly() => new((interval, capacity) => new MetricsSampler(interval, capacity,
        () => new(DateTime.Now, 30, 50, 70, 2UL << 30, 8UL << 30)), () => new SessionFpsRecorder(findPresentMon: () => null));

    [Fact]
    public void Templates_and_history_controls_preserve_identity_selections_and_render_in_both_themes_and_modes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(SessionWorkspaceUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var first = AppState.GamePath!; var second = Path.Combine(root, "other", "FixtureGame.exe");
            foreach (var s in PerformanceSessionStore.LoadHistory()) PerformanceSessionStore.Delete(s.Header.Id);
            var a = SessionConditionTests.Example("训练前") with { Conditions = SessionConditionRules.CaptureGame(first,
                new SessionConditions(Width: 1440, Height: 1080, Quality: "高", Scene: "训练场", TargetSeconds: 60)) };
            var b = SessionConditionTests.Example("训练后") with { Conditions = a.Conditions, Fps = null };
            var c = SessionConditionTests.Example("另一款游戏") with { Conditions = SessionConditionRules.CaptureGame(second,
                new SessionConditions(Quality: "低", Scene: "城市")) };
            var legacy = SessionConditionTests.Example("旧记录") with { Conditions = null, Fps = null };
            foreach (var s in new[] { a, b, c, legacy }) PerformanceSessionStore.Save(s);
            using var session = HardwareOnly();
            var page = new SessionView(session);
            var main = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show(); main.NavigateTo("session");
            ((ContentControl)main.FindName("PageHost")).Content = page;
            TextBox Box(string name) => (TextBox)page.FindName(name);
            Button Button(string name) => (Button)page.FindName(name);
            void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            string Text(string name) => ((TextBlock)page.FindName(name)).Text;
            static string Id(object item) => (string)item.GetType().GetProperty("Id")!.GetValue(item)!;
            try
            {
                var history = (ListBox)page.FindName("HistoryList");
                DlssFeedbackTests.PumpUntil(() => history.Items.Count == 4);
                ((Expander)page.FindName("GettingStarted")).IsExpanded = false;
                Box("ResolutionWidthBox").Text = "1440"; Box("ResolutionHeightBox").Text = "1080";
                Box("QualityBox").Text = "高"; Box("SceneBox").Text = "训练场"; Box("TargetSecondsBox").Text = "60";
                Click("SaveTemplateButton"); Assert.Equal("训练场", SessionTemplateStore.Load(first)!.Scene);
                Box("SceneBox").Text = "草稿"; Click("LoadTemplateButton"); Assert.Equal("训练场", Box("SceneBox").Text);
                Box("ResolutionHeightBox").Text = "0"; Click("SaveTemplateButton");
                Assert.Contains(Str.T("Str.ConditionInvalid"), Text("TemplateStatus"));
                Assert.Equal(1080, SessionTemplateStore.Load(first)!.Height); Click("LoadTemplateButton");
                AppState.GamePath = second; Click("LoadTemplateButton");
                Assert.Equal(Str.T("Str.SessionTemplateMissing"), Text("TemplateStatus"));
                Assert.Equal("训练场", Box("SceneBox").Text); // A missing template never clears a draft.
                AppState.GamePath = first;
                var compareA = (ComboBox)page.FindName("CompareA"); var compareB = (ComboBox)page.FindName("CompareB");
                history.SelectedItem = history.Items.Cast<object>().Single(v => Id(v) == a.Id);
                compareA.SelectedItem = compareA.Items.Cast<object>().Single(v => Id(v) == a.Id);
                compareB.SelectedItem = compareB.Items.Cast<object>().Single(v => Id(v) == b.Id);
                Box("HistorySearchBox").Text = "训练场";
                Assert.Equal(2, history.Items.Count); Assert.Equal(a.Id, Id(history.SelectedItem));
                Assert.Equal(a.Id, Id(compareA.SelectedItem)); Assert.Equal(b.Id, Id(compareB.SelectedItem));
                ((CheckBox)page.FindName("HistoryFpsOnly")).IsChecked = true;
                Assert.Single(history.Items.Cast<object>()); Assert.Equal(a.Id, Id(history.SelectedItem));
                Assert.Null(compareB.SelectedItem); Assert.Single(compareB.Items.Cast<object>());
                Assert.True(Button("ExportJsonButton").IsEnabled);
                Box("HistorySearchBox").Text = "没有这个场景";
                Assert.Empty(history.Items); Assert.Null(compareA.SelectedItem);
                Assert.False(Button("ExportJsonButton").IsEnabled); Assert.False(Button("DeleteButton").IsEnabled);
                Assert.Equal(Str.T("Str.SessionHistoryCount", 0, 4), Text("HistoryCountText"));
                Assert.Equal(Visibility.Visible, ((TextBlock)page.FindName("HistoryEmptyText")).Visibility);
                Click("ResetHistoryFiltersButton"); Assert.Equal(4, history.Items.Count);
                var gameFilter = (ComboBox)page.FindName("HistoryGameFilter");
                Assert.Equal(4, gameFilter.Items.Count); // all + two same-name paths + unknown
                var choices = gameFilter.Items.Cast<object>().Select(v => (string)v.GetType().GetProperty("Label")!.GetValue(v)!).ToArray();
                Assert.Equal(choices.Length, choices.Distinct().Count());
                gameFilter.SelectedItem = gameFilter.Items.Cast<object>().Single(v => (string)v.GetType().GetProperty("Key")!.GetValue(v)! == c.Conditions!.GameKey);
                Assert.Single(history.Items.Cast<object>()); Assert.Equal(c.Id, Id(history.Items[0]));
                Click("ResetHistoryFiltersButton");
                history.SelectedItems.Add(history.Items.Cast<object>().Single(v => Id(v) == a.Id));
                history.SelectedItems.Add(history.Items.Cast<object>().Single(v => Id(v) == b.Id));
                Assert.True(Button("DeleteButton").IsEnabled); Assert.False(Button("ExportCsvButton").IsEnabled);
                Box("HistorySearchBox").Text = "训练场";
                Assert.Equal(2, history.SelectedItems.Count);
                foreach (var mode in new[] { "studio", "console" })
                foreach (var theme in new[] { "light", "dark" })
                {
                    main.SetDisplayMode(mode); ThemeManager.SetMode(theme); main.UpdateLayout();
                    foreach (var card in new[] { "SamplingCard", "HistoryCard" })
                    {
                        var element = (FrameworkElement)page.FindName(card); element.BringIntoView(); main.UpdateLayout();
                        Render(element, card + "-" + mode + "-" + theme);
                        if (mode == "studio" && theme == "light") Render((FrameworkElement)main.FindName("ChromeRoot"), card + "-viewport");
                    }
                    Assert.True(Box("HistorySearchBox").ActualWidth > 150);
                    Assert.True(gameFilter.ActualWidth >= 200);
                    Assert.InRange(Math.Abs(Box("HistorySearchBox").ActualHeight - gameFilter.ActualHeight), 0, 1);
                }
                // Only filtered, visible selections may be deleted; exercise the actual confirmation path in this isolated data root.
                DialogService.PrepareWindowForTest = dialog =>
                {
                    DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => dialog.DialogResult = true);
                };
                Click("DeleteButton");
                DlssFeedbackTests.PumpUntil(() => history.Items.Count == 0);
                Assert.Equal(new[] { c.Id, legacy.Id }.Order(), PerformanceSessionStore.LoadHistory().Select(s => s.Header.Id).Order());
                DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
                Assert.True(session.Start("Busy", new SessionConditions()));
                Assert.False(Button("SaveTemplateButton").IsEnabled); Click("DeleteTemplateButton");
                Assert.NotNull(SessionTemplateStore.Load(first)); session.Stop(false);
                Box("SceneBox").Text = "删除模板后保留草稿";
                Click("DeleteTemplateButton"); Assert.Null(SessionTemplateStore.Load(first));
                Assert.Equal("删除模板后保留草稿", Box("SceneBox").Text);
            }
            finally { main.Close(); }
        });
    }

    [Fact]
    public void Automatic_recording_uses_game_template_without_manual_timer_and_preserves_original_game_selection()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(SessionWorkspaceUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((_, root) =>
        {
            var selection = AppState.GamePath;
            var path = Path.Combine(root, "auto", "game.exe");
            SessionTemplateStore.Save(path, new SessionConditions(Width: 1440, Height: 1080, Quality: "高", Scene: "训练", TargetSeconds: 1));
            GameProcessObservation observation = new(null);
            using var session = HardwareOnly();
            using var automation = new AutoRecordingService(session, () => (true, new[] { path }), _ => observation, () => false);
            void Poll() { var task = automation.PollAsync(); DlssFeedbackTests.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
            Poll(); observation = new(new(50, 10)); Poll();
            Assert.True(session.IsRunning); Assert.Equal(selection, AppState.GamePath);
            Assert.Equal(1440, session.Conditions!.Width); Assert.Equal("训练", session.Conditions.Scene);
            Assert.Null(session.Conditions.TargetSeconds); Assert.Equal(1, SessionTemplateStore.Load(path)!.TargetSeconds);
            var before = PerformanceSessionStore.LoadHistory().Count;
            observation = new(null); Poll(); Assert.False(session.IsRunning);
            Assert.Equal(before + 1, PerformanceSessionStore.LoadHistory().Count);
            var saved = PerformanceSessionStore.LoadAll().Single(s => s.Conditions?.Scene == "训练");
            Assert.Equal(session.Conditions, saved.Conditions);
        });
    }

    [Fact]
    public void Template_storage_keeps_other_games_and_original_bytes_on_invalid_future_or_failed_writes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(SessionWorkspaceUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((_, root) =>
        {
            var a = Path.Combine(root, "one", "game.exe"); var b = Path.Combine(root, "two", "game.exe");
            SessionTemplateStore.Save(a, new SessionConditions(Scene: "One"));
            SessionTemplateStore.Save(b, new SessionConditions(Scene: "Two"));
            Assert.Equal("One", SessionTemplateStore.Load(a.ToUpperInvariant())!.Scene);
            var file = SessionTemplateStore.FileFor(a); var bytes = File.ReadAllBytes(file);
            Assert.Throws<ArgumentException>(() => SessionTemplateStore.Save(a, new SessionConditions(Width: 1)));
            Assert.Equal(bytes, File.ReadAllBytes(file));
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => SessionTemplateStore.Save(a, new SessionConditions(Scene: "Changed")));
                Assert.True(error is IOException or UnauthorizedAccessException, $"Expected sharing/replace refusal, got {error}");
            }
            Assert.Equal(bytes, File.ReadAllBytes(file));
            File.WriteAllText(file, "{\"SchemaVersion\":99}");
            Assert.Throws<InvalidDataException>(() => SessionTemplateStore.Load(a));
            Assert.Throws<InvalidDataException>(() => SessionTemplateStore.Save(a, new SessionConditions()));
            Assert.Equal("{\"SchemaVersion\":99}", File.ReadAllText(file));
            SessionTemplateStore.Delete(a); Assert.Null(SessionTemplateStore.Load(a));
            Assert.Equal("Two", SessionTemplateStore.Load(b)!.Scene);
            Assert.Throws<ArgumentException>(() => SessionTemplateStore.Save("relative.exe", new SessionConditions()));
        });
    }

    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(drawing);
        var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "session-workspace-20261029", "ui"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
