using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class PreparationWorkflowTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "preparation037");
    private static PreparationRun Example(string? game, int i = 0) => new(Guid.NewGuid().ToString("N"), DateTimeOffset.Now.AddSeconds(i), 3.2,
        SessionConditionRules.CaptureGame(game, null).GameKey, "训练游戏", "完整整理", false, null,
        [new("整理", true, "completed")]);

    [Fact]
    public void History_retains_bounded_results_filters_exports_and_preserves_unknown_or_locked_data()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationWorkflowTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var game = AppState.GamePath!;
            for (var i = 0; i < 51; i++) PreparationHistory.Append(Example(game, i));
            var runs = PreparationHistory.Read(); Assert.Equal(50, runs.Count);
            Assert.True(runs[0].StartedAt > runs[^1].StartedAt);
            var partial = Example(Path.Combine(root, "other.exe"), 60) with { Steps = [new("filter", true, "done"), new("memory", false, @"failed C:\Private\secret.txt", "failed")] };
            PreparationHistory.Append(partial);
            var window = new PreparationHistoryWindow(game) { Width = 760, Height = 480 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var list = (ListBox)window.FindName("RunList"); Assert.Equal(49, list.Items.Count);
                ((ComboBox)window.FindName("ScopeBox")).SelectedIndex = 1; Assert.Equal(50, list.Items.Count);
                Assert.Equal(Str.T("Str.PreparePartial"), ((TextBlock)window.FindName("OutcomeText")).Text);
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(window, "history-" + theme); }
                var export = Path.Combine(Output, "export-" + Guid.NewGuid().ToString("N") + ".json");
                Assert.True(window.ExportSelected(export)); var text = File.ReadAllText(export);
                Assert.DoesNotContain("Private", text); Assert.Contains("<path>", text); Assert.Contains("memory", text);
                var bytes = File.ReadAllBytes(PreparationHistory.FilePath);
                Assert.False(window.ExportSelected(PreparationHistory.FilePath)); Assert.Equal(bytes, File.ReadAllBytes(PreparationHistory.FilePath));
                using (File.Open(Path.ChangeExtension(PreparationHistory.FilePath, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Assert.Throws<IOException>(() => PreparationHistory.Append(Example(game, 70)));
                Assert.Equal(bytes, File.ReadAllBytes(PreparationHistory.FilePath));
                var future = JsonNode.Parse(File.ReadAllText(PreparationHistory.FilePath))!; future["SchemaVersion"] = 99;
                File.WriteAllText(PreparationHistory.FilePath, future.ToJsonString()); var original = File.ReadAllBytes(PreparationHistory.FilePath);
                Assert.Throws<InvalidDataException>(() => PreparationHistory.Append(Example(game, 80)));
                Assert.Equal(original, File.ReadAllBytes(PreparationHistory.FilePath));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Copy_plan_previews_without_writes_preserves_target_display_and_refuses_changed_target_or_invalid_source()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationWorkflowTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var target = AppState.GamePath!; var source = Path.Combine(root, "source.exe");
            StateStore.AddGame(target); StateStore.AddGame(source);
            var a = MultiDisplayColorTests.Screen("A", "DISPLAY1", 0, "0001", true);
            var b = MultiDisplayColorTests.Screen("B", "DISPLAY2", 1, "0002", false);
            DisplayTopology.Override = () => [a, b];
            var original = new GamePreparationOptions { Configured = true, ApplyVibrance = true, VibrancePercent = 55, DisplayIdentity = DisplayTopology.Identity(a) };
            var copied = original with { FullMemoryCleanup = true, AllStandby = true, ApplyIcc = true, IccPreset = IccFilterPreset.Warm,
                VibrancePercent = 77, DisplayIdentity = DisplayTopology.Identity(b) };
            PreparationPlanStore.Save(target, original); PreparationPlanStore.Save(source, copied);
            var before = File.ReadAllBytes(PreparationPlanStore.FileFor(target));
            var dialog = new GamePreparationWindow(); app.MainWindow = dialog;
            WithDialog(dialog, () =>
            {
                ((Expander)dialog.FindName("CopyPlanSection")).IsExpanded = true;
                ((ComboBox)dialog.FindName("SourcePlanBox")).SelectedIndex = 1; Click(dialog, "CopyPlanButton");
                Assert.Equal(2, ((ComboBox)dialog.FindName("MemoryMode")).SelectedIndex);
                Assert.Equal(77, ((Slider)dialog.FindName("VibranceSlider")).Value);
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(dialog, "copy-plan-" + theme); }
                Assert.Equal(before, File.ReadAllBytes(PreparationPlanStore.FileFor(target)));
                dialog.Close();
            });
            Assert.Equal(original, PreparationPlanStore.Resolve(target).Options);
            var save = new GamePreparationWindow();
            WithDialog(save, () =>
            {
                ((ComboBox)save.FindName("SourcePlanBox")).SelectedIndex = 1; Click(save, "CopyPlanButton");
                AppState.GamePath = source; Click(save, "SaveButton");
                Assert.Equal(Str.T("Str.PlanGameChanged"), ((TextBlock)save.FindName("StatusText")).Text);
                Assert.Equal(before, File.ReadAllBytes(PreparationPlanStore.FileFor(target)));
                AppState.GamePath = target; Click(save, "SaveButton");
            });
            var result = PreparationPlanStore.Resolve(target).Options;
            Assert.Equal(copied with { DisplayIdentity = original.DisplayIdentity }, result);
            Assert.Equal(copied, PreparationPlanStore.Resolve(source).Options);
            File.WriteAllText(PreparationPlanStore.FileFor(source), "{\"SchemaVersion\":99}");
            var invalid = new GamePreparationWindow();
            WithDialog(invalid, () =>
            {
                ((ComboBox)invalid.FindName("SourcePlanBox")).SelectedIndex = 1;
                Assert.False(((Button)invalid.FindName("CopyPlanButton")).IsEnabled);
                Click(invalid, "CopyPlanButton"); Assert.Equal(result, PreparationPlanStore.Resolve(target).Options); invalid.Close();
            });
        });
    }

    [Fact]
    public void Home_execution_persists_actual_service_results_and_stop_and_history_failure_does_not_repeat_cleanup()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(PreparationWorkflowTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            PreparationPlanStore.Save(AppState.GamePath, new() { Configured = true, FullMemoryCleanup = true });
            var memory = new MemoryCleanupTests.FakeMemory(); var apps = new PreparationMemoryIntegrationTests.Apps();
            var page = new ConsoleView(new(new(memory, () => false, () => true), new(apps)));
            var host = new Window { Width = 1000, Height = 720, Content = page }; app.MainWindow = host;
            DlssFeedbackTests.PrepareBackgroundWindow(host); host.Show();
            try
            {
                Click(page, "StartPreparationButton"); DlssFeedbackTests.PumpUntil(() => File.Exists(PreparationHistory.FilePath));
                DlssFeedbackTests.PumpUntil(() => ((Button)page.FindName("StartPreparationButton")).IsEnabled);
                Assert.True(Assert.Single(PreparationHistory.Read()).Succeeded); Assert.Equal(1, memory.Purges);
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                apps.OnTrim = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(4))); };
                Click(page, "StartPreparationButton"); DlssFeedbackTests.PumpUntil(() => entered.IsSet);
                Click(page, "CancelPreparationButton"); release.Set();
                DlssFeedbackTests.PumpUntil(() => ((Button)page.FindName("StartPreparationButton")).IsEnabled);
                var stopped = PreparationHistory.Read()[0]; Assert.True(stopped.Stopped); Assert.False(stopped.Succeeded);
                Assert.Equal(1, memory.Purges); Assert.Equal(2, PreparationHistory.Read().Count);
                File.WriteAllText(PreparationHistory.FilePath, "future-data"); apps.OnTrim = null;
                Click(page, "StartPreparationButton");
                DlssFeedbackTests.PumpUntil(() => memory.Purges == 2 && ((Button)page.FindName("StartPreparationButton")).IsEnabled);
                Assert.Equal("future-data", File.ReadAllText(PreparationHistory.FilePath)); Assert.Equal(2, memory.Purges);
                Assert.Contains(Str.T("Str.PreparationHistorySaveFailed", Str.T("Str.PreparationHistoryUnreadable")), ((TextBlock)page.FindName("PreparationStatus")).Text);
                Render(host, "home-history-save-failure");
            }
            finally { host.Close(); }
        });
    }

    private static void WithDialog(GamePreparationWindow dialog, Action action)
    {
        DlssFeedbackTests.PrepareBackgroundWindow(dialog); Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (!((Button)dialog.FindName("SaveButton")).IsEnabled) return;
            timer.Stop(); try { action(); } catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        timer.Start(); try { dialog.ShowDialog(); } finally { timer.Stop(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Click(FrameworkElement element, string name) => ((Button)element.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Render(Window window, string name)
    {
        window.UpdateLayout(); var root = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(drawing); Directory.CreateDirectory(Path.Combine(Output, "ui"));
        using var file = File.Create(Path.Combine(Output, "ui", name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
    }
}
