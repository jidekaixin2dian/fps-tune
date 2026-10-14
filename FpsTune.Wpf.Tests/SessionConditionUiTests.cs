using System.Runtime.ExceptionServices;
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
public sealed class SessionConditionUiTests
{
    [Fact]
    public void Actual_buttons_save_timed_conditions_keep_start_game_reuse_history_and_show_comparison_warnings()
    {
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        { DlssFeedbackTests.RunIsolated(testType: typeof(SessionConditionUiTests)); return; }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "fpstune-condition-ui-" + Guid.NewGuid().ToString("N"));
            var previousGame = AppState.GamePath;
            Application? app = null; Window? window = null;
            try
            {
                UserDataPaths.RootOverride = root; PerformanceSessionStore.OverrideDir = Path.Combine(root, "sessions");
                AppState.GamePath = @"C:\ConditionUiGame\first.exe";
                PerformanceSessionStore.Save(SessionConditionTests.Example());
                PerformanceSessionStore.Save(SessionConditionTests.Example("After") with {
                    Conditions = SessionConditionTests.Example().Conditions! with { Width = 2560 }, Fps = null });
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = DlssFeedbackTests.LoadResources() };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                var page = new SessionView();
                window = new Window { Width = 1080, Height = 850, Content = page };
                app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
                var history = (ListBox)page.FindName("HistoryList");
                DlssFeedbackTests.PumpUntil(() => history.Items.Count == 2);
                Box("SessionNameBox").Text = "Timed UI run";
                Box("ResolutionWidthBox").Text = "1920"; Box("ResolutionHeightBox").Text = "1080";
                Box("QualityBox").Text = "High"; Box("SceneBox").Text = "Training route";
                Box("TargetSecondsBox").Text = "0";
                GetButton("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(App.SessionService.IsRunning);
                Assert.Equal(Str.T("Str.ConditionInvalid"), ((TextBlock)page.FindName("RunStateText")).Text);
                Box("TargetSecondsBox").Text = "1";
                GetButton("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(App.SessionService.IsRunning);
                Assert.False(((StackPanel)page.FindName("ConditionsPanel")).IsEnabled);
                var capturedGame = App.SessionService.Conditions!.GameKey;
                AppState.GamePath = @"C:\ConditionUiGame\second.exe";
                DlssFeedbackTests.PumpUntil(() => !App.SessionService.IsRunning && history.Items.Count == 3);
                var saved = PerformanceSessionStore.LoadAll().Single(s => s.Name == "Timed UI run");
                Assert.Equal(capturedGame, saved.Conditions!.GameKey);
                Assert.Equal(1, saved.Conditions.TargetSeconds);
                Assert.Equal("Training route", saved.Conditions.Scene);
                Assert.True(saved.Samples.Count > 0);
                Assert.True(((StackPanel)page.FindName("ConditionsPanel")).IsEnabled);
                history.SelectedItem = history.Items.Cast<object>().Single(i => Name(i) == "Baseline");
                GetButton("ReuseConditionsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("60", Box("TargetSecondsBox").Text);
                Assert.Equal("1920", Box("ResolutionWidthBox").Text);
                Assert.Equal("High / DLSS Quality / uncapped", Box("QualityBox").Text);
                var a = (ComboBox)page.FindName("CompareA"); var b = (ComboBox)page.FindName("CompareB");
                foreach (var language in new[] { LangService.ZhCn, LangService.EnUs })
                {
                    LangService.Apply(language);
                    a.SelectedItem = null;
                    a.SelectedItem = a.Items.Cast<object>().Single(i => Name(i) == "Baseline");
                    b.SelectedItem = b.Items.Cast<object>().Single(i => Name(i) == "After");
                    var warning = (TextBlock)page.FindName("CompareConditionsText");
                    Assert.Equal(Visibility.Visible, warning.Visibility);
                    Assert.Contains(Str.T("Str.ConditionDifferent", Str.T("Str.ConditionResolution")), warning.Text);
                    Assert.Contains(Str.T("Str.ConditionFpsMissing"), warning.Text);
                    Assert.DoesNotContain("!Str.", warning.Text);
                    Render((FrameworkElement)page.FindName("SamplingCard"), "sampling-" + language);
                    Render((FrameworkElement)page.FindName("ComparisonCard"), "compare-" + language);
                }
                TextBox Box(string name) => (TextBox)page.FindName(name);
                Button GetButton(string name) => (Button)page.FindName(name);
                static string? Name(object item) => (string?)item.GetType().GetProperty("Name")!.GetValue(item);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                if (App.SessionService.IsRunning) App.SessionService.Cancel();
                window?.Close(); app?.Shutdown(); UserDataPaths.RootOverride = null; PerformanceSessionStore.OverrideDir = null; AppState.GamePath = previousGame;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Session conditions UI did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Render(FrameworkElement element, string name)
    {
        element.BringIntoView();
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(drawing);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.True(Enumerable.Range(0, bitmap.PixelWidth * bitmap.PixelHeight).Count(i => pixels[i * 4 + 3] > 0) > bitmap.PixelWidth * bitmap.PixelHeight / 2,
            "Rendered card must contain actual pixels, including when it was below the viewport.");
        var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "release028", "ui"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
