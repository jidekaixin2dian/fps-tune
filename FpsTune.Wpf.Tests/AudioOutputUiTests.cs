using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 声音均衡器「输出到」行的离屏渲染证据与真实交互：默认输出、设备不匹配时的状态行与「跟到 Y」按钮，
/// 以及没装引擎时的如实说明。设备与引擎全部注入，写入落在临时目录。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class AudioOutputUiTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Audio_output_row_shows_the_default_device_mismatch_and_follows()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(AudioOutputUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var engineDir = Path.Combine(root, "engine");
            Directory.CreateDirectory(engineDir);
            File.WriteAllText(Path.Combine(engineDir, "config.txt"), "# 用户原有配置\n");
            var headset = new AudioOutput(Guid.NewGuid().ToString("D"), "耳机（测试）");
            var speakers = new AudioOutput(Guid.NewGuid().ToString("D"), "音箱（测试）");
            var settings = SettingsService.Current;
            var follow0 = settings.AudioFollowDefaultOutput;
            var draft0 = settings.EqualizerDraft;
            AudioEqualizerService.EngineOverride = new(engineDir, engineDir, "unused");
            AudioEqualizerService.OutputsOverride = () => [headset, speakers];
            AudioEqualizerService.DefaultOutputOverride = () => speakers;
            AudioEqualizerService.ResetDefaultOutputTracking();
            Window? window = null, missingWindow = null;
            try
            {
                settings.AudioFollowDefaultOutput = true;
                settings.EqualizerDraft = null;
                // 方案写在耳机，系统默认输出已经换成音箱：这就是要提示的不匹配状态。
                AudioEqualizerService.Apply(new EqualizerSettings(headset.Id, new double[10]));

                var panel = new AudioEqualizerPanel();
                window = new Window { Width = 900, Height = 1180, Content = panel };
                app.MainWindow = window;
                DlssFeedbackTests.PrepareBackgroundWindow(window);
                window.Show();
                DlssFeedbackTests.PumpUntil(() => panel.DefaultOutputText.Text.Length > 0);

                Assert.Equal(Str.T("Str.EqDefaultOutput", speakers.Name), panel.DefaultOutputText.Text);
                Assert.Equal(Str.T("Str.EqFollowSystemDefault"), ((AudioOutput)panel.OutputCombo.SelectedItem).Name);
                Assert.Equal(Visibility.Visible, panel.MismatchText.Visibility);
                Assert.Equal(Str.T("Str.EqOutputMismatch", headset.Name, speakers.Name), panel.MismatchText.Text);
                Assert.Equal(Visibility.Collapsed, panel.WrittenToText.Visibility);
                Assert.Equal(Visibility.Visible, panel.FollowNowButton.Visibility);
                Assert.Equal(Str.T("Str.EqFollowButton", speakers.Name), (string)panel.FollowNowButton.Content);
                Assert.True(panel.FollowNowButton.IsEnabled);
                Screenshot(panel, "audio-output-light", "light");
                Screenshot(panel, "audio-output-dark", "dark");

                // 「跟到 音箱」：写到新的默认设备，状态行变成「均衡器写在 音箱」。
                panel.FollowNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => panel.FollowNowButton.Visibility == Visibility.Collapsed);
                Assert.Equal(Str.T("Str.EqWrittenTo", speakers.Name), panel.WrittenToText.Text);
                Assert.Equal(Visibility.Visible, panel.WrittenToText.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.MismatchText.Visibility);
                Assert.Equal(Str.T("Str.EqFollowed", speakers.Name), panel.EqStatus.Text);
                Assert.Contains(Guid.Parse(speakers.Id).ToString("B"), File.ReadAllText(Path.Combine(engineDir, "FpsTune-Equalizer.txt")));

                // 没装引擎：如实说没检测到，应用按钮不可用，不假装写成功。
                AudioEqualizerService.EngineOverride = null;
                AudioEqualizerService.EngineProbeOverride = () => null;
                var missing = new AudioEqualizerPanel();
                missingWindow = new Window { Width = 900, Height = 1180, Content = missing };
                DlssFeedbackTests.PrepareBackgroundWindow(missingWindow);
                missingWindow.Show();
                DlssFeedbackTests.PumpUntil(() => missing.EngineStatus.Text.Length > 0);
                Assert.Equal(Str.T("Str.EqEngineMissing"), missing.EngineStatus.Text);
                Assert.False(missing.EqApplyButton.IsEnabled);
                Assert.Equal(Str.T("Str.EqDefaultOutput", speakers.Name), missing.DefaultOutputText.Text);
                Screenshot(missing, "audio-output-no-engine-light", "light");
                Assert.DoesNotContain("!Str.", missing.EngineStatus.Text + missing.DefaultOutputText.Text + missing.MismatchText.Text);
            }
            finally
            {
                window?.Close();
                missingWindow?.Close();
                AudioEqualizerService.EngineOverride = null;
                AudioEqualizerService.EngineProbeOverride = null;
                AudioEqualizerService.OutputsOverride = null;
                AudioEqualizerService.DefaultOutputOverride = null;
                AudioEqualizerService.ResetDefaultOutputTracking();
                AudioEqualizerService.TakeoverPaused = false;
                settings.AudioFollowDefaultOutput = follow0;
                settings.EqualizerDraft = draft0;
            }
        });
    }

    private static void Screenshot(FrameworkElement element, string name, string theme)
    {
        ThemeManager.SetMode(theme);
        var expected = (Color)ColorConverter.ConvertFromString(theme == "light" ? "#F7F9FC" : "#0C1015")!;
        DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)Application.Current.FindResource("AppBackdropBrush")).Color == expected);
        RenderTo(element, name);
    }

    private static void RenderTo(FrameworkElement element, string name)
    {
        var frame = new DispatcherFrame();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(visual);
        var dir = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "ref-align-297", "shots-audio");
        Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
