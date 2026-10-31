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
public sealed class CompetitiveTuningTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "competitive037");
    private static string Ini(string width = "1920", string height = "1080", bool valorant = false) =>
        $"[ignored]\nResolutionSizeX=9999\n[{(valorant ? "/Script/ShooterGame.ShooterGameUserSettings" : "/Script/Engine.GameUserSettings")}]\nResolutionSizeX={width}\nResolutionSizeY={height}\nFullscreenMode=0\nbUseVSync=False\nFrameRateLimit=0.000000\n";
    private static string Cs2 => "\"video.cfg\"\n{\n\"setting.defaultres\" \"1440\"\n\"setting.defaultresheight\" \"1080\"\n\"setting.fullscreen\" \"1\"\n\"setting.mat_vsync\" \"0\"\n\"setting.refreshrate_numerator\" \"239760\"\n\"setting.refreshrate_denominator\" \"1000\"\n}\n";
    private static string Write(string path, string text) { path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }

    [Fact]
    public void Three_game_config_readers_discover_scoped_accounts_and_keep_missing_conflicting_or_invalid_values_unknown()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(CompetitiveTuningTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((_, root) =>
        {
            var local = Path.Combine(root, "local"); var steam = Path.Combine(root, "Steam");
            var a = Write(Path.Combine(local, "VALORANT/Saved/Config/account-a/WindowsClient/GameUserSettings.ini"), Ini(valorant: true));
            var b = Write(Path.Combine(local, "VALORANT/Saved/Config/account-b/WindowsClient/GameUserSettings.ini"), Ini("2560", "1440", true));
            Assert.Equal(2, GameVideoConfig.Discover(Path.Combine(root, "VALORANT-Win64-Shipping.exe"), CompetitiveGame.Valorant, local, []).Count);
            var deltaExe = Write(Path.Combine(root, "DeltaForce/Binaries/Win64/DeltaForceClient-Win64-Shipping.exe"), "never run");
            var deltaConfig = Write(Path.Combine(root, "DeltaForce/Saved/Config/WindowsClient/GameUserSettings.ini"), Ini());
            Assert.Contains(deltaConfig, GameVideoConfig.Discover(deltaExe, CompetitiveGame.DeltaForce, local, []));
            var cs2 = Write(Path.Combine(steam, "userdata/123/730/local/cfg/cs2_video.txt"), Cs2);
            Assert.Single(GameVideoConfig.Discover(Path.Combine(root, "cs2.exe"), CompetitiveGame.Cs2, local, [steam]));
            var bytes = File.ReadAllBytes(a); var value = GameVideoConfig.Read(a, CompetitiveGame.Valorant);
            Assert.Equal("1920", value.Values["width"]); Assert.Equal("Exclusive", value.Values["mode"]);
            Assert.Equal("239.76", GameVideoConfig.Read(cs2, CompetitiveGame.Cs2).Values["refresh"]);
            Assert.False(GameVideoConfig.Read(cs2, CompetitiveGame.Cs2).Values.ContainsKey("fps"));
            var partial = GameVideoConfig.Parse(Ini() + "ResolutionSizeX=2560\nResolutionSizeY=NaN\n", CompetitiveGame.DeltaForce);
            Assert.False(partial.ContainsKey("width")); Assert.False(partial.ContainsKey("height")); Assert.Equal("0", partial["fps"]);
            Assert.Throws<InvalidDataException>(() => GameVideoConfig.Parse("[wrong]\nResolutionSizeX=1920", CompetitiveGame.DeltaForce));
            Assert.Throws<ArgumentException>(() => GameTuning.Cs2Commands("240;quit", "60"));
            Assert.Throws<ArgumentException>(() => GameTuning.Cs2Commands("NaN", "60"));
            Assert.Equal("fps_max 0; fps_max_ui 60", GameTuning.Cs2Commands("0", "60"));
            Assert.Equal(bytes, File.ReadAllBytes(a)); Assert.True(GameVideoConfig.ValidValues(value.Values));
            Assert.Null(GameTuning.Identify("VALORANT.exe")); Assert.Null(GameTuning.Identify("csgo.exe"));
        });
    }

    [Fact]
    public void Driver_combo_commits_selected_items_once_preserves_DLSS_and_detects_ignored_saves_external_changes_and_invalid_requests()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(CompetitiveTuningTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((_, root) =>
        {
            var api = new DisplayQualityTests.FakeNvdrsApi(); DisplayQualityService.ApiOverride = () => api;
            DisplayQualityService.BackupDirOverride = Path.Combine(root, "drs");
            foreach (var exe in new[] { "cs2.exe", "VALORANT-Win64-Shipping.exe", "DeltaForceClient-Win64-Shipping.exe" })
            {
                var p = api.AddProfile(exe, exe); p.Settings[DisplayQualityService.VSyncModeId] = 0x47814940;
                p.Settings[DisplayQualityService.FrameRateLimitId] = 200; p.Settings[DisplayQualityService.PreRenderLimitId] = 3;
                p.Settings[DisplayQualityService.DlssSrPresetId] = 5;
                DisplayQualityService.ApplyDlssPreset(exe, DlssPreset.PresetK); var saves = api.SaveCount;
                DisplayQualityService.ApplyCompetitiveSettings(exe, new(false, false, true, null, 30));
                Assert.Equal(saves + 1, api.SaveCount); Assert.Equal(200u, p.Settings[DisplayQualityService.FrameRateLimitId]);
                Assert.Equal(3u, p.Settings[DisplayQualityService.PreRenderLimitId]); Assert.Equal(11u, p.Settings[DisplayQualityService.DlssSrPresetId]);
                Assert.True(DisplayQualityService.RestoreCompetitiveSettings(exe)); Assert.Equal(0x47814940u, p.Settings[DisplayQualityService.VSyncModeId]);
                Assert.Equal(11u, p.Settings[DisplayQualityService.DlssSrPresetId]); Assert.True(DisplayQualityService.HasRestorableBackup(exe));
                Assert.False(DisplayQualityService.HasCompetitiveBackup(exe));
            }
            var before = api.SaveCount;
            Assert.Throws<ArgumentException>(() => DisplayQualityService.ApplyCompetitiveSettings("cs2.exe", new(true, false, false, null, null)));
            Assert.Throws<ArgumentOutOfRangeException>(() => DisplayQualityService.ApplyCompetitiveSettings("cs2.exe", new(false, false, false, 19, 30)));
            Assert.Equal(before, api.SaveCount);
            api.SimulateSaveIgnored = true;
            Assert.Throws<InvalidOperationException>(() => DisplayQualityService.ApplyCompetitiveSettings("cs2.exe", new(false, false, true, 240, 30)));
            api.SimulateSaveIgnored = false;
            DisplayQualityService.ApplyCompetitiveSettings("cs2.exe", new(true, true, false, 240, 30));
            var cs = api.GetProfile("cs2.exe")!;
            cs.Settings[DisplayQualityService.FrameRateLimitId] = 333;
            Assert.Throws<InvalidOperationException>(() => DisplayQualityService.RestoreCompetitiveSettings("cs2.exe"));
            Assert.Equal(333u, cs.Settings[DisplayQualityService.FrameRateLimitId]); Assert.True(DisplayQualityService.HasCompetitiveBackup("cs2.exe"));
        });
    }

    [Fact]
    public void Config_selection_snapshots_and_recording_keep_account_identity_refuse_stale_files_and_do_not_start_or_save_templates()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(CompetitiveTuningTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var path = Write(Path.Combine(root, "VALORANT-Win64-Shipping.exe"), "never run"); AppState.GamePath = path; StateStore.AddGame(path);
            var a = Write(Path.Combine(root, "account-a/GameUserSettings.ini"), Ini(valorant: true));
            var b = Write(Path.Combine(root, "account-b/GameUserSettings.ini"), Ini("2560", "1440", true));
            var window = new GameTuningWindow(path, [a, b]) { Width = 860, Height = 580 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                Assert.Equal(-1, ((ComboBox)window.FindName("SourceBox")).SelectedIndex);
                Assert.False(((Button)window.FindName("PrepareRecordButton")).IsEnabled);
                ((ComboBox)window.FindName("SourceBox")).SelectedIndex = 0;
                DlssFeedbackTests.PumpUntil(() => ((Button)window.FindName("SaveBaselineButton")).IsEnabled);
                Click(window, "SaveBaselineButton"); Assert.Equal("1920", GameConfigBaselines.Load(path, a)!.Values["width"]);
                Write(a, Ini("1280", "960", true)); Click(window, "SaveBaselineButton");
                Assert.Equal(Str.T("Str.TuningConfigChanged"), ((TextBlock)window.FindName("StatusText")).Text);
                Assert.Equal("1920", GameConfigBaselines.Load(path, a)!.Values["width"]);
                Click(window, "ReloadButton"); DlssFeedbackTests.PumpUntil(() => ((Button)window.FindName("SaveBaselineButton")).IsEnabled);
                Assert.Contains("2", ((TextBlock)window.FindName("BaselineStatus")).Text);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); ((ScrollViewer)window.FindName("ContentScroll")).ScrollToHome(); Render(window, "valorant-config-" + theme);
                    ((ScrollViewer)window.FindName("ContentScroll")).ScrollToEnd(); Render(window, "valorant-config-bottom-" + theme);
                }
                ((ComboBox)window.FindName("SourceBox")).SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => ((Button)window.FindName("SaveBaselineButton")).IsEnabled);
                Assert.Null(GameConfigBaselines.Load(path, b)); Assert.Equal("1920", GameConfigBaselines.Load(path, a)!.Values["width"]);
                var recordDialog = new GameTuningWindow(path, [b]) { Owner = window };
                DlssFeedbackTests.PrepareBackgroundWindow(recordDialog); Exception? modalError = null;
                recordDialog.Loaded += (_, _) => recordDialog.Dispatcher.BeginInvoke(() =>
                {
                    try { DlssFeedbackTests.PumpUntil(() => ((Button)recordDialog.FindName("PrepareRecordButton")).IsEnabled); Click(recordDialog, "PrepareRecordButton"); }
                    catch (Exception ex) { modalError = ex; recordDialog.Close(); }
                });
                Assert.True(recordDialog.ShowDialog()); Assert.Null(modalError);
                var conditions = Assert.IsType<SessionConditions>(recordDialog.Recording);
                using var service = new PerformanceSessionService(); var page = new SessionView(service);
                var host = new Window { Width = 1000, Height = 700, Content = page }; DlssFeedbackTests.PrepareBackgroundWindow(host); host.Show();
                try
                {
                    page.LoadGameConditions(path, conditions);
                    Assert.Equal("2560", ((TextBox)page.FindName("ResolutionWidthBox")).Text);
                    Assert.Equal("1440", ((TextBox)page.FindName("ResolutionHeightBox")).Text);
                    Assert.Equal("120", ((TextBox)page.FindName("TargetSecondsBox")).Text); Assert.False(service.IsRunning);
                    Assert.Null(SessionTemplateStore.Load(path));
                }
                finally { host.Close(); }
                var file = Directory.GetFiles(Path.Combine(root, "game-tuning"), "*.json").Single();
                var json = JsonNode.Parse(File.ReadAllText(file))!; json["SchemaVersion"] = 99; File.WriteAllText(file, json.ToJsonString()); var original = File.ReadAllBytes(file);
                Assert.Throws<InvalidDataException>(() => GameConfigBaselines.Save(path, GameVideoConfig.Read(a, CompetitiveGame.Valorant)));
                Assert.Equal(original, File.ReadAllBytes(file));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Specialized_windows_render_each_game_and_cs2_commands_and_reviewed_driver_apply_restore_are_real_interactions()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(CompetitiveTuningTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var api = new DisplayQualityTests.FakeNvdrsApi(); DisplayQualityService.ApiOverride = () => api;
            foreach (var (exe, kind) in new[] { ("cs2.exe", CompetitiveGame.Cs2), ("DeltaForceClient-Win64-Shipping.exe", CompetitiveGame.DeltaForce) })
            {
                var path = Write(Path.Combine(root, exe), "never run"); AppState.GamePath = path; StateStore.AddGame(path);
                var p = api.AddProfile(exe, exe); var savesBefore = api.SaveCount;
                var file = Write(Path.Combine(root, exe + ".txt"), kind == CompetitiveGame.Cs2 ? Cs2 : Ini()); var bytes = File.ReadAllBytes(file);
                var window = new GameTuningWindow(path, [file]) { Width = 860, Height = 580 }; app.MainWindow = window;
                DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
                try
                {
                    DlssFeedbackTests.PumpUntil(() => ((Button)window.FindName("ApplyDriverButton")).IsEnabled);
                    Assert.Equal(kind == CompetitiveGame.Cs2 ? Visibility.Visible : Visibility.Collapsed, ((RadioButton)window.FindName("Cs2Tab")).Visibility);
                    ((RadioButton)window.FindName("DriverTab")).IsChecked = true;
                    ((ComboBox)window.FindName("SyncBox")).SelectedIndex = 1;
                    var blocked = window.ChangeDriverAsync(false); DlssFeedbackTests.PumpUntil(() => blocked.IsCompleted); blocked.GetAwaiter().GetResult();
                    Assert.Equal(savesBefore, api.SaveCount); // Unconfirmed VRR does not open an apply confirmation or save.
                    ((CheckBox)window.FindName("VrrConfirmed")).IsChecked = true;
                    ((CheckBox)window.FindName("BackgroundCheck")).IsChecked = true;
                    foreach (var theme in new[] { "light", "dark" })
                    {
                        ThemeManager.SetMode(theme); ((ScrollViewer)window.FindName("ContentScroll")).ScrollToHome(); Render(window, kind + "-driver-" + theme);
                        ((ScrollViewer)window.FindName("ContentScroll")).ScrollToEnd(); Render(window, kind + "-driver-bottom-" + theme);
                    }
                    DialogService.PrepareWindowForTest = dialog => { DlssFeedbackTests.PrepareBackgroundWindow(dialog); dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => dialog.DialogResult = true); };
                    var apply = window.ChangeDriverAsync(false); DlssFeedbackTests.PumpUntil(() => apply.IsCompleted); apply.GetAwaiter().GetResult();
                    Assert.Equal(30u, p.Settings[DisplayQualityService.BackgroundFrameRateLimitId]);
                    var restore = window.ChangeDriverAsync(true); DlssFeedbackTests.PumpUntil(() => restore.IsCompleted); restore.GetAwaiter().GetResult();
                    Assert.False(p.Settings.ContainsKey(DisplayQualityService.BackgroundFrameRateLimitId));
                    if (kind == CompetitiveGame.Cs2)
                    {
                        ((RadioButton)window.FindName("Cs2Tab")).IsChecked = true; string? copied = null; window.CopyText = text => copied = text;
                        ((TextBox)window.FindName("Cs2Foreground")).Text = "360"; ((TextBox)window.FindName("Cs2Menu")).Text = "90"; Click(window, "CopyCommandButton");
                        Assert.Equal("fps_max 360; fps_max_ui 90", copied);
                        foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(window, "cs2-commands-" + theme); }
                    }
                    Assert.Equal(bytes, File.ReadAllBytes(file));
                }
                finally { window.Close(); }
            }
            api.SimulateMissingDriver = true;
            var unavailable = new GameTuningWindow(Path.Combine(root, "cs2.exe"), []); app.MainWindow = unavailable;
            DlssFeedbackTests.PrepareBackgroundWindow(unavailable); unavailable.Show();
            try
            {
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)unavailable.FindName("DriverCurrent")).Text.Length > 0);
                Assert.False(((Button)unavailable.FindName("ApplyDriverButton")).IsEnabled);
            }
            finally { unavailable.Close(); }
        });
    }
    private static void Click(FrameworkElement root, string name) => ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Render(Window window, string name)
    {
        window.UpdateLayout(); var root = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen())
        { dc.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight)); dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight)); }
        bitmap.Render(drawing); Directory.CreateDirectory(Path.Combine(Output, "ui")); using var file = File.Create(Path.Combine(Output, "ui", name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
    }
}
