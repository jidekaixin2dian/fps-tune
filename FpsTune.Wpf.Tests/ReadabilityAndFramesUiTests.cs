using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ReadabilityAndFramesUiTests
{
    [Fact]
    public void Corner_presets_and_custom_values_update_existing_pages_and_survive_reload()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var window = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                window.SetDisplayMode("studio"); window.NavigateTo("settings"); window.UpdateLayout();
                var page = (SettingsView)((ContentControl)window.FindName("PageHost")).Content;
                DlssFeedbackTests.PumpUntil(() => ((RadioButton)page.FindName("CornerLargeRadio")).IsChecked == true);
                ((RadioButton)page.FindName("CornerClassicRadio")).IsChecked = true;
                Assert.Equal(new CornerRadius(12), app.FindResource("RadiusCard"));
                window.SetDisplayMode("console"); Assert.Equal(new CornerRadius(6), app.FindResource("RadiusCard"));
                ((RadioButton)page.FindName("CornerLargeRadio")).IsChecked = true;
                Assert.Equal(new CornerRadius(18), app.FindResource("RadiusCard"));
                ((Slider)page.FindName("CornerCardSlider")).Value = 25;
                SettingsService.Load(); Assert.Equal("custom", SettingsService.Current.Corners.Preset);
                Assert.Equal(25, SettingsService.Current.Corners.Card);
                ThemeManager.SetMode("light"); window.NavigateTo("display"); window.NavigateTo("settings");
                Assert.Equal(new CornerRadius(25), app.FindResource("RadiusCard"));
                Assert.False(((RadioButton)page.FindName("CornerLargeRadio")).IsChecked);
                Assert.Equal(0, new CornerPreferences("custom", -5).Normalize().Card);
            }
            finally { window.Close(); }
        });
    }
    [Fact]
    public void Rapid_navigation_presents_cached_pages_without_a_transparent_animation()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var previous = UiPerformance.LowSpec;
            UiPerformance.LowSpec = false;
            var window = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var host = (ContentControl)window.FindName("PageHost");
                window.NavigateTo("display");
                var cached = host.Content;
                window.NavigateTo("opt");
                window.NavigateTo("settings");
                window.NavigateTo("display");
                Assert.Same(cached, host.Content);
                Assert.Equal(1, host.Opacity);
                Assert.False(host.HasAnimatedProperties);
                Assert.Equal(0, host.RenderTransform.Value.OffsetX);
                Assert.True(((RadioButton)window.FindName("NavDisplay")).IsChecked);
            }
            finally { window.Close(); UiPerformance.LowSpec = previous; }
        });
    }
    [Fact]
    public void Eight_k_wallpaper_is_normalized_and_visible_without_modifying_source()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, root) =>
        {
            var path = Environment.GetEnvironmentVariable("FPSTUNE_WALLPAPER_TEST_IMAGE");
            if (string.IsNullOrWhiteSpace(path)) { path = Path.Combine(root, "八千像素·壁纸.jpg"); WallpaperTests.CreateEightKJpeg(path); }
            var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
            var previous = SettingsService.Current.WallpaperEnabled; var opacity = SettingsService.Current.WallpaperOpacity;
            MainWindow? window = null;
            try
            {
                WallpaperService.Import(path);
                var image = WallpaperService.Load()!;
                Assert.Equal(1920, image.PixelWidth); Assert.InRange(image.PixelHeight, 976, 978); Assert.True(image.IsFrozen);
                Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
                SettingsService.Current.WallpaperEnabled = true; SettingsService.Current.WallpaperOpacity = 0.25;
                window = new MainWindow { Width = 1180, Height = 800 }; app.MainWindow = window;
                DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
                var backdrop = (Image)window.FindName("WallpaperBackdrop");
                Assert.Equal(Visibility.Visible, backdrop.Visibility); Assert.Same(image, backdrop.Source);
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render((FrameworkElement)window.FindName("ChromeRoot"), "wallpaper-" + theme); }
                File.WriteAllText(Path.Combine(Output(), "wallpaper-import.json"), JsonSerializer.Serialize(new { image.PixelWidth, image.PixelHeight, image.IsFrozen, SourceUnchanged = true, OriginalSha256 = Convert.ToHexString(hash) }));
            }
            finally { window?.Close(); SettingsService.Current.WallpaperEnabled = previous; SettingsService.Current.WallpaperOpacity = opacity; }
        });
    }

    [Fact]
    public void Disclosure_header_hits_arrow_text_and_blank_space_and_animates_both_directions()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var guide = new Expander { Header = "展开与收起", Content = new TextBlock { Text = "内容", Height = 100 }, Margin = new Thickness(16) };
            var window = new Window { Content = guide, Width = 640, Height = 360 };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            var panel = (Views.Controls.DisclosurePanel)guide.Template.FindName("ExpandSite", guide);
            var header = (System.Windows.Controls.Primitives.ToggleButton)guide.Template.FindName("HeaderSite", guide);
            var provider = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(header)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
            void CheckHeader()
            {
                window.UpdateLayout();
                foreach (var x in new[] { 7d, 42d, header.ActualWidth - 8 })
                {
                    var hit = (DependencyObject)header.InputHitTest(new Point(x, header.ActualHeight / 2));
                    while (hit is not null && !ReferenceEquals(hit, header)) hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(header, hit);
                }
                Assert.Equal(new Thickness(0), ((Border)header.Template.FindName("HeaderBorder", header)).BorderThickness);
                Assert.NotNull(header.FocusVisualStyle); Assert.True(header.Focusable);
            }
            try
            {
                Views.Controls.DisclosurePanel.AnimationEnabledOverride = true;
                CheckHeader(); provider.Toggle();
                Assert.True(guide.IsExpanded);
                DlssFeedbackTests.PumpUntil(() => panel.Reveal is > 0 and < 1);
                window.UpdateLayout(); Assert.InRange(panel.ActualHeight, 0.01, 111.99);
                DlssFeedbackTests.PumpUntil(() => panel.Reveal == 1);
                CheckHeader(); provider.Toggle(); Assert.False(guide.IsExpanded);
                DlssFeedbackTests.PumpUntil(() => panel.Reveal is > 0 and < 1);
                Assert.Equal(Visibility.Visible, panel.Visibility); Assert.False(panel.IsEnabled);
                provider.Toggle(); // Reverse a collapse in progress without hiding expanded content.
                DlssFeedbackTests.PumpUntil(() => panel.Reveal == 1); Assert.True(guide.IsExpanded);
                provider.Toggle(); DlssFeedbackTests.PumpUntil(() => panel.Visibility == Visibility.Collapsed);
                CheckHeader();
                Views.Controls.DisclosurePanel.AnimationEnabledOverride = null; // Low-spec test mode: immediate, still clickable.
                provider.Toggle(); Assert.Equal(1, panel.Reveal); Assert.True(panel.IsEnabled);
                provider.Toggle(); Assert.Equal(Visibility.Collapsed, panel.Visibility);
            }
            finally { Views.Controls.DisclosurePanel.AnimationEnabledOverride = null; window.Close(); }
        });
    }

    [Fact]
    public void Detection_sections_filter_without_long_page_and_gpu_presets_preview_readable_device_names()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var data = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new {
                gamePath = AppState.GamePath,
                hardware = new { cpu = "Fixture CPU", gpu = "NVIDIA GeForce RTX 4060", ramGB = 16, os = "Windows 11", isLaptop = false, isAdmin = false },
                checks = new[] { new { name = "内存频率", status = "ok", message = "5600 MHz" }, new { name = "VC++ 运行库", status = "attention", message = "缺少 x86" } },
                items = ItemCatalog.All.Where(d => d.Available).Select(d => new { id = d.Id, name = d.Name, desc = d.Description, sideEffect = d.SideEffect,
                    admin = d.Admin, reboot = d.Reboot, optimized = false, current = "测试原值", group = d.Group })
            }))!.AsObject();
            StateStore.SaveDetect(data);
            var window = new MainWindow { Width = 1180, Height = 800 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            GpuIdentityService.DeviceIdsOverride = () => [];
            try
            {
                window.NavigateTo("detect"); window.UpdateLayout();
                var detect = (DetectView)((ContentControl)window.FindName("PageHost")).Content;
                DlssFeedbackTests.PumpUntil(() => detect.IsLoaded); window.UpdateLayout();
                var checkList = (ItemsControl)detect.FindName("CheckList");
                Assert.Equal(2, Descendants(checkList).OfType<System.Windows.Shapes.Ellipse>().Count());
                Assert.DoesNotContain(Descendants(checkList).OfType<TextBlock>(), t => t.Text.Contains('●'));
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(detect, "detect-overview-" + theme); }
                Assert.Equal(((System.Windows.Media.FontFamily)app.FindResource("FontBody")).Source, ((TextBlock)detect.FindName("GamePathText")).FontFamily.Source);
                var path = (TextBlock)detect.FindName("GamePathText"); Assert.DoesNotContain("Consolas", path.FontFamily.Source);
                Descendants(detect).OfType<RadioButton>().Single(b => b.Tag as string == "items").IsChecked = true;
                var list = (ListBox)detect.FindName("DetectItemList");
                ((TextBox)detect.FindName("DetectFilterText")).Text = "Wi-Fi";
                Assert.Single(list.Items); Assert.Contains("Wi-Fi", ((OptimizationItemViewModel)list.Items[0]).Name);
                ((TextBox)detect.FindName("DetectFilterText")).Text = "no matching item"; Assert.Empty(list.Items);
                ((TextBox)detect.FindName("DetectFilterText")).Text = "";
                ((ComboBox)detect.FindName("DetectGroup")).SelectedIndex = 3;
                Assert.All(list.Items.Cast<OptimizationItemViewModel>(), item => Assert.Equal("网络", item.Group));
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(detect, "detect-items-" + theme); }
                Descendants(detect).OfType<RadioButton>().Single(b => b.Tag as string == "report").IsChecked = true;
                Assert.True(((Grid)detect.FindName("DetectReportSection")).IsVisible);
                window.NavigateTo("display"); window.UpdateLayout();
                var display = (DisplayQualityView)((ContentControl)window.FindName("PageHost")).Content;
                DlssFeedbackTests.PumpUntil(() => ((Grid)display.FindName("FrameControls")).IsEnabled);
                var devices = (ComboBox)display.FindName("GpuIdentityDevices");
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)display.FindName("GpuIdentityStatus")).Text.Length > 0);
                devices.ItemsSource = new[] { new GpuIdentityTarget("PCI\\VEN_10DE&DEV_1234\\fixture", "NVIDIA GeForce RTX 4060", "@oem.inf;NVIDIA GeForce RTX 4060", "fixture") }; devices.SelectedIndex = 0;
                Descendants(display).OfType<RadioButton>().Single(b => b.Tag as string == "identity").IsChecked = true;
                var presets = (ListBox)display.FindName("GpuIdentityPresets"); presets.SelectedIndex = 0; window.UpdateLayout();
                Assert.Equal(8, presets.Items.Count); Assert.Equal("NVIDIA GeForce GTX 750 Ti", ((TextBlock)display.FindName("GpuIdentityPreviewName")).Text);
                Assert.Equal(new[] { "GTX 750 Ti", "GTX 1050 Ti" }, presets.Items.Cast<GpuNamePreset>().Where(p => p.IsRecommended).Select(p => p.Name));
                Assert.All(presets.Items.Cast<GpuNamePreset>().Take(2), p => Assert.True(p.IsRecommended));
                Assert.True(devices.ActualWidth <= 600);
                Assert.Contains(Descendants(devices).OfType<TextBlock>(), t => t.Text == "NVIDIA GeForce RTX 4060");
                Assert.DoesNotContain(Descendants(devices).OfType<TextBlock>(), t => t.Text.Contains("InstanceId"));
                ((TextBlock)display.FindName("GpuIdentityStatus")).Text = Str.T("Str.GpuIdentityReady");
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(display, "gpu-presets-" + theme); }
            }
            finally { GpuIdentityService.DeviceIdsOverride = null; window.Close(); }
        });
    }

    [Fact]
    public void Navigation_weight_stays_constant_and_all_pages_render_with_readable_guides_and_distinct_series()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var window = new MainWindow { Width = 1180, Height = 800 };
            window.NavigateTo("opt"); app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var nav = (RadioButton)window.FindName("NavAb"); nav.ApplyTemplate();
                var label = Descendants(nav).OfType<TextBlock>().Single(t => t.Text == Str.T("Str.NavAb"));
                var weight = label.FontWeight; var size = label.FontSize;
                nav.IsChecked = true; window.UpdateLayout();
                Assert.Equal(weight, label.FontWeight); Assert.Equal(size, label.FontSize);
                var report = new List<object>();
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme);
                    Assert.NotEqual(((SolidColorBrush)app.FindResource("AccentBrush")).Color,
                        ((SolidColorBrush)app.FindResource("ChartSecondaryBrush")).Color);
                    foreach (var route in new[] { "home", "detect", "opt", "display", "session", "ab", "backup", "tools", "settings" })
                    {
                        window.NavigateTo(route); window.UpdateLayout();
                        var page = (FrameworkElement)((ContentControl)window.FindName("PageHost")).Content;
                        DlssFeedbackTests.PumpUntil(() => page.IsLoaded);
                        var titleKey = route switch { "detect" => "Str.NavDetect", "opt" => "Str.OptimizeTitle", "display" => "Str.DisplayTitle",
                            "session" => "Str.NavSession", "ab" => "Str.NavAb", "backup" => "Str.NavBackup", "tools" => "Str.NavTools", "settings" => "Str.NavSettings", _ => null };
                        if (titleKey is not null)
                            Assert.Contains(Descendants(page).OfType<TextBlock>(), t => t.Text == Str.T(titleKey) && t.FontSize == 22);
                        if (page is ConsoleView)
                            foreach (var name in new[] { "CpuText", "MemoryText", "GpuText", "TunedText" })
                                Assert.Equal("Segoe UI, Microsoft YaHei UI", ((TextBlock)page.FindName(name)).FontFamily.Source);
                        if (page is DisplayQualityView)
                            DlssFeedbackTests.PumpUntil(() => ((Grid)page.FindName("FrameControls")).IsEnabled);
                        if (page is SessionView or AbExperimentView)
                        {
                            var guide = (Expander)page.FindName("GettingStarted");
                            Assert.True(guide.IsExpanded); guide.IsExpanded = false; Assert.False(guide.IsExpanded); guide.IsExpanded = true;
                        }
                        Capture(window, route + "-" + theme, report);
                        if (page is SettingsView)
                            for (var i = 0; i < 6; i++) { ((RadioButton)page.FindName("SettingsNav" + i)).IsChecked = true; Capture(window, "settings" + i + "-" + theme, report); }
                        if (page is MaintenanceView)
                            foreach (var name in new[] { "StorageNav", "ProcessesNav", "TroubleshootNav" })
                            { ((RadioButton)page.FindName(name)).IsChecked = true; Capture(window, "tools-" + name + "-" + theme, report); }
                        if (page is DisplayQualityView)
                            foreach (var tab in Descendants(page).OfType<RadioButton>().Where(b => b.Tag is string s && new[] { "dlss", "vibicc", "drs", "identity", "advice" }.Contains(s)).ToArray())
                            { tab.IsChecked = true; Capture(window, "display-" + tab.Tag + "-" + theme, report); }
                    }
                }
                File.WriteAllText(Path.Combine(Output(), "readability-audit.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Actual_frame_buttons_validate_commit_restore_only_caps_and_keep_failed_edits_visible()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, root) =>
        {
            var exe = Path.GetFileName(AppState.GamePath)!;
            var api = (DisplayQualityTests.FakeNvdrsApi)DisplayQualityService.ApiOverride!();
            var profile = api.AddProfile("existing", exe);
            profile.Settings[DisplayQualityService.FrameRateLimitId] = 90;
            DisplayQualityService.ApplyDlssPreset(exe, DlssPreset.PresetK);
            var page = new DisplayQualityView();
            var window = new Window { Width = 1180, Height = 800, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible);
                if (dialog is not null) ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            try
            {
                Descendants(page).OfType<RadioButton>().Single(b => b.Tag as string == "drs").IsChecked = true;
                var controls = (Grid)page.FindName("FrameControls");
                var apply = (Button)page.FindName("FrameApplyButton"); var restore = (Button)page.FindName("FrameRestoreButton");
                var state = (TextBlock)page.FindName("FrameStateText");
                DlssFeedbackTests.PumpUntil(() => controls.IsEnabled);
                var baselineSaves = api.SaveCount;
                var mainCheck = (CheckBox)page.FindName("MainCapCheck"); var mainBox = (TextBox)page.FindName("MainCapBox");
                mainCheck.IsChecked = true; mainBox.Text = "19";
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(baselineSaves, api.SaveCount); Assert.Equal(Str.T("Str.FrameRange"), state.Text);
                mainBox.Text = "144"; ((CheckBox)page.FindName("BackgroundCapCheck")).IsChecked = true;
                ((TextBox)page.FindName("BackgroundCapBox")).Text = "30";
                timer.Start(); apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(controls.IsEnabled);
                DlssFeedbackTests.PumpUntil(() => state.Text == Str.T("Str.FrameApplied") && controls.IsEnabled);
                Assert.Equal(baselineSaves + 1, api.SaveCount);
                Assert.Equal(144u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
                Assert.Equal(30u, profile.Settings[DisplayQualityService.BackgroundFrameRateLimitId]);
                Assert.True(restore.IsEnabled);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); apply.BringIntoView(); window.UpdateLayout();
                    Render((FrameworkElement)window.Content, "frames-" + theme);
                }
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => state.Text == Str.T("Str.FrameRestored") && controls.IsEnabled);
                Assert.Equal(90u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
                Assert.False(profile.Settings.ContainsKey(DisplayQualityService.BackgroundFrameRateLimitId));
                Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
                api.SimulateSaveIgnored = true;
                mainCheck.IsChecked = true; mainBox.Text = "165";
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => controls.IsEnabled && state.Text.StartsWith(Str.T("Str.FrameFailed", ""), StringComparison.Ordinal));
                Assert.Equal("165", mainBox.Text); Assert.True(mainCheck.IsChecked);
                Assert.Equal(90u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
            }
            finally { timer.Stop(); window.Close(); }
        });
    }

    [Fact]
    public void Memory_button_handles_busy_elevation_pending_success_and_failures_without_real_cleanup()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var busy = false; var admin = true;
            var api = new MemoryCleanupTests.FakeMemory();
            var page = new MaintenanceView(new MemoryCleanupService(api, () => busy, () => admin));
            var window = new Window { Width = 1180, Height = 800, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            var button = (Button)page.FindName("MemoryCleanButton");
            var status = (TextBlock)page.FindName("MemoryResult");
            var refresh = (Button)page.FindName("RefreshButton");
            try
            {
                ((RadioButton)page.FindName("ProcessesNav")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => button.IsEnabled && ((TextBlock)page.FindName("MemoryAvailable")).Text.Contains("5.00"));
                busy = true; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Str.T("Str.MemoryBusy"), status.Text); Assert.Equal(0, api.Purges); busy = false;
                admin = false;
                var cancel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                cancel.Tick += (_, _) =>
                {
                    var dialog = app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible);
                    if (dialog is not null) ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                cancel.Start();
                try { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                finally { cancel.Stop(); }
                Assert.Equal(0, api.Purges); admin = true;
                var mode = (ComboBox)page.FindName("MemoryMode"); mode.SelectedIndex = 1;
                using var hold = new ManualResetEventSlim(); api.Hold = hold;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => Volatile.Read(ref api.Purges) == 1);
                Assert.False(button.IsEnabled); Assert.False(refresh.IsEnabled); Assert.False(mode.IsEnabled); Assert.True(api.AllPriorities);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, api.Purges);
                hold.Set(); DlssFeedbackTests.PumpUntil(() => button.IsEnabled);
                Assert.Equal(Str.T("Str.MemoryCacheChange", "1024.0 MiB", "0.0 MiB", "5.00 GiB", "5.00 GiB"), status.Text);
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(page, "memory-" + theme); }
                api.Hold = null; api.PurgeError = new System.ComponentModel.Win32Exception(1314);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); DlssFeedbackTests.PumpUntil(() => button.IsEnabled);
                Assert.StartsWith(Str.T("Str.MemoryFailed", ""), status.Text); Assert.True(refresh.IsEnabled);
                api.PurgeError = null; api.FailRead = api.Reads + 2;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); DlssFeedbackTests.PumpUntil(() => button.IsEnabled);
                Assert.Contains("snapshot unavailable", status.Text); Assert.Contains("刷新", status.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Unassigned_legacy_vibrance_receipt_keeps_actual_controls_available_and_new_restore_point_independent()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            using var screens = new MultiDisplayColorTests();
            Directory.CreateDirectory(DigitalVibranceService.BackupDirOverride!);
            var receipt = Path.Combine(DigitalVibranceService.BackupDirOverride!, "vibrance.json");
            const string original = "{\"Level\":90}"; File.WriteAllText(receipt, original);
            var page = new DisplayQualityView(); var window = new Window { Width = 1180, Height = 800, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            var apply = (Button)page.FindName("VibApplyButton"); var restore = (Button)page.FindName("VibRestoreButton");
            var state = (TextBlock)page.FindName("VibStateText"); var slider = (Slider)page.FindName("VibSlider");
            var failureShown = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible);
                if (dialog is not null)
                {
                    if (((TextBlock)dialog.FindName("TitleTextBlock")).Text == Str.T("Str.OperationFailed"))
                    {
                        Assert.Contains(Str.T("Str.DisplayWriteUnconfirmed"), ((TextBlock)dialog.FindName("MessageTextBlock")).Text);
                        Assert.True(((Button)dialog.FindName("CopyDetailsButton")).IsVisible); failureShown = true;
                    }
                    ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            try
            {
                Descendants(page).OfType<RadioButton>().Single(b => b.Tag as string == "vibicc").IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => apply.IsEnabled && slider.Value == 30);
                Assert.True(((StackPanel)page.FindName("VibSupportedPanel")).IsVisible);
                Assert.False(restore.IsEnabled); Assert.Contains(Str.T("Str.VibranceLegacyUnassigned"), state.Text);
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(page, "vibrance-legacy-" + theme); }
                foreach (var tab in Descendants(page).OfType<RadioButton>().Where(b => b.Tag is string))
                { tab.ApplyTemplate(); Assert.Null(tab.Template.FindName("FocusRing", tab)); }
                timer.Start(); slider.Value = 78; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => apply.IsEnabled && slider.Value == 78 && restore.IsEnabled);
                Assert.Equal(78, screens.Levels["A"]); Assert.Equal(original, File.ReadAllText(receipt));
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => apply.IsEnabled && slider.Value == 30 && !restore.IsEnabled);
                Assert.Equal(30, screens.Levels["A"]); Assert.Equal(original, File.ReadAllText(receipt));
                screens.IgnoreVibWrite = true; slider.Value = 89; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => apply.IsEnabled && failureShown);
                Assert.Equal(30, screens.Levels["A"]); Assert.Equal(original, File.ReadAllText(receipt));
                Assert.Contains(Str.T("Str.DisplayWriteUnconfirmed"), state.Text);
            }
            finally { timer.Stop(); window.Close(); }
        });
    }

    [Fact]
    public void Preparation_configuration_saves_without_writes_then_one_click_runs_and_partial_failure_shows_copyable_details()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            using var screens = new MultiDisplayColorTests(); var memory = new MemoryCleanupTests.FakeMemory();
            var page = new ConsoleView(new GamePreparationService(new MemoryCleanupService(memory, () => false, () => true)));
            var window = new Window { Width = 1180, Height = 800, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            var start = (Button)page.FindName("StartPreparationButton"); var status = (TextBlock)page.FindName("PreparationStatus");
            var configure = (Button)page.FindName("ConfigurePreparationButton");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            var configured = false; var failureShown = false; string? copied = null;
            timer.Tick += (_, _) =>
            {
                if (app.Windows.OfType<GamePreparationWindow>().FirstOrDefault(w => w.IsVisible) is { } dialog)
                {
                    var displays = (ComboBox)dialog.FindName("DisplayBox"); if (displays.Items.Count != 2) return;
                    Assert.Contains(Str.T("Str.PrepareNeedsDetection"), ((TextBlock)dialog.FindName("BasicGuide")).Text);
                    Assert.Contains("1.", ((TextBlock)dialog.FindName("BasicGuide")).Text);
                    Assert.Contains("\n2.", ((TextBlock)dialog.FindName("BasicGuide")).Text);
                    ((CheckBox)dialog.FindName("IccCheck")).IsChecked = true;
                    ((CheckBox)dialog.FindName("VibranceCheck")).IsChecked = true;
                    ((Slider)dialog.FindName("VibranceSlider")).Value = 76;
                    ((ComboBox)dialog.FindName("MemoryMode")).SelectedIndex = 1; displays.SelectedIndex = 1;
                    foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render((FrameworkElement)dialog.Content, "preparation-config-" + theme); }
                    ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); configured = true;
                }
                if (app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible) is { } error)
                {
                    Assert.Equal(Str.T("Str.PreparePartial"), ((TextBlock)error.FindName("TitleTextBlock")).Text);
                    var body = ((TextBlock)error.FindName("MessageTextBlock")).Text;
                    Assert.Contains(Str.T("Str.PrepareStepDone"), body); Assert.Contains(Str.T("Str.PrepareStepAttention"), body);
                    Assert.Contains(Str.T("Str.DisplayWriteUnconfirmed"), body);
                    error.CopyDetails = value => copied = value;
                    var copy = (Button)error.FindName("CopyDetailsButton"); Assert.True(copy.IsVisible);
                    copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains(body, copied);
                    Assert.Equal(Str.T("Str.ErrorCopied"), copy.Content);
                    foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render((FrameworkElement)error.Content, "preparation-failed-" + theme); }
                    error.CopyDetails = _ => throw new InvalidOperationException("clipboard busy");
                    copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(Str.T("Str.ErrorCopyFailed"), copy.Content);
                    failureShown = true; ((Button)error.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            try
            {
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(page, "preparation-home-" + theme); }
                timer.Start(); configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(configured); Assert.Equal(0, memory.Purges); Assert.Empty(screens.Writes);
                SettingsService.Load(); var saved = SettingsService.Current.Preparation;
                Assert.True(saved.Configured && saved.CleanMemory && saved.AllStandby && saved.ApplyIcc && saved.ApplyVibrance);
                Assert.Equal("B", saved.DisplayIdentity); Assert.Equal(76, saved.VibrancePercent);
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.False(start.IsEnabled);
                DlssFeedbackTests.PumpUntil(() => start.IsEnabled && status.Text == Str.T("Str.PrepareDone"));
                Assert.Equal(1, memory.Purges); Assert.Equal(76, screens.Levels["B"]); Assert.Equal(30, screens.Levels["A"]);
                screens.IgnoreVibWrite = true; SettingsService.Current.Preparation = saved with { VibrancePercent = 83 };
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => start.IsEnabled && failureShown);
                Assert.Equal(Str.T("Str.PreparePartial"), status.Text); Assert.Equal(2, memory.Purges);
                Assert.Equal(76, screens.Levels["B"]); Assert.Single(ScreenColorService.BackupTargets());
            }
            finally { timer.Stop(); window.Close(); }
        });
    }

    [Fact]
    public void Home_actions_align_at_supported_sizes_and_selection_review_and_busy_game_choice_stay_consistent()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, root) =>
        {
            var original = AppState.GamePath!;
            var unavailable = ItemCatalog.All.Single(d => !d.Available);
            var unavailableItem = new OptimizationItem(unavailable.Id, unavailable.DisplayName, unavailable.DisplayDescription, unavailable.DisplaySideEffect,
                unavailable.Admin, unavailable.Reboot, false, "not available", false, unavailable.Group);
            AppState.Items.Add(unavailableItem);
            var second = Path.Combine(root, DeltaPreparation.GameExecutable); File.WriteAllText(second, "not executable");
            StateStore.AddGame(original); StateStore.AddGame(second);
            AppState.DetectJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { gamePath = original }));
            StateStore.MarkOnboardingSeen();
            var window = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            var page = (ConsoleView)((ContentControl)window.FindName("PageHost")).Content;
            var combo = (ComboBox)page.FindName("GameSwitcher");
            var availableCount = ItemCatalog.All.Count(d => d.Available);
            Assert.Equal(Str.T("Str.HomeSelectionCount", OptimizationCatalog.ResolvePreset("balanced").Count, availableCount), ((TextBlock)page.FindName("SelectionText")).Text);
            Assert.Equal("0/" + availableCount, ((TextBlock)page.FindName("TunedText")).Text);
            Assert.Equal(Str.T("Str.Unavailable"), new OptimizationItemViewModel(unavailableItem).StatusText);
            var report = new List<object>(); var oldBusy = GameContextService.IsBusyProvider;
            try
            {
                foreach (var mode in new[] { "studio", "console" })
                foreach (var theme in new[] { "light", "dark" })
                foreach (var size in new[] { new Size(1180, 720), new Size(1360, 860) })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    SettingsService.Current.ThemeMode = theme; window.SetDisplayMode(mode); window.UpdateLayout();
                    var actions = ((WrapPanel)page.FindName("PreparationActions")).Children.OfType<Button>().ToArray();
                    Assert.Equal(6, actions.Length);
                    var tops = actions.Select(b => b.TranslatePoint(new Point(0, 0), page).Y).ToArray();
                    Assert.InRange(tops.Max() - tops.Min(), 0, 0.1);
                    foreach (var action in actions)
                    {
                        Assert.Equal(36, action.ActualHeight); var at = action.TranslatePoint(new Point(), page);
                        Assert.InRange(at.X, 0, page.ActualWidth - action.ActualWidth + 0.1);
                        Assert.InRange(at.Y, 0, page.ActualHeight - action.ActualHeight + 0.1);
                    }
                    Assert.Single(Descendants(combo).OfType<System.Windows.Shapes.Path>()); // Dropdown arrow only.
                    var review = (Button)page.FindName("ReviewButton"); var bottom = review.TranslatePoint(new Point(), page);
                    Assert.True(bottom.Y + review.ActualHeight <= page.ActualHeight);
                    Capture(window, $"home-audit-{mode}-{size.Width}-{theme}", report);
                }
                // Search changes visibility only; selecting a previously excluded item enters custom mode.
                var search = (TextBox)page.FindName("SearchBox"); search.Text = "Wi-Fi"; window.UpdateLayout();
                var list = (ListBox)page.FindName("ItemList"); Assert.Single(list.Items);
                var toggle = Descendants(list).OfType<CheckBox>().Single();
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
                Assert.True(((RadioButton)page.FindName("CustomPreset")).IsChecked); Assert.Contains("wifi-performance-ac", AppState.SelectedIds);
                search.Text = ""; window.UpdateLayout(); Assert.Contains("wifi-performance-ac", AppState.SelectedIds);
                ((RadioButton)page.FindName("BalancedPreset")).IsChecked = true;
                Assert.Equal(OptimizationCatalog.ResolvePreset("balanced").Order(), AppState.SelectedIds.Order());
                Descendants(page).OfType<RadioButton>().Single(b => b.Tag as string == "safe-only").IsChecked = true;
                Assert.Equal(OptimizationCatalog.ResolvePreset("safe-only").Order(), AppState.SelectedIds.Order());
                ((Button)page.FindName("PrepareDeltaButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                var optimization = Assert.IsType<OptimizeView>(((ContentControl)window.FindName("PageHost")).Content);
                Assert.Equal(new[] { "dvr-off", "game-mode", "gpu-pref" }, optimization.Items.Where(i => i.IsChecked).Select(i => i.Id).Order());
                ((RadioButton)optimization.FindName("PresetFull")).IsChecked = true;
                Assert.False(optimization.Items.Single(i => i.Id == unavailable.Id).IsChecked);
                Assert.Equal(availableCount, optimization.Items.Count(i => i.IsChecked));
                window.NavigateTo("home"); window.UpdateLayout();
                Render(page, "home-before-busy-dark");
                // Rejecting a game switch must also restore the visible selected label.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    if (app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible) is { } dialog)
                        ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                try
                {
                    GameContextService.IsBusyProvider = () => true; timer.Start();
                    combo.SelectedItem = combo.Items.OfType<GameSwitcherItem>().Single(g => g.ExePath == second);
                    Assert.Equal(original, AppState.GamePath);
                    Assert.Equal(original, ((GameSwitcherItem)combo.SelectedItem).ExePath);
                }
                finally { timer.Stop(); GameContextService.IsBusyProvider = oldBusy; }
                File.WriteAllText(Path.Combine(Output(), "home-interaction-audit.json"), JsonSerializer.Serialize(report));
            }
            finally { GameContextService.IsBusyProvider = oldBusy; window.Close(); }
        });
    }

    [Fact]
    public void Toggle_switch_has_inset_thumb_clickable_track_and_continuous_animation_in_both_directions()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ReadabilityAndFramesUiTests)); return; }
        RunUi((app, _) =>
        {
            var toggle = new CheckBox { Content = "开关示例", Style = (Style)app.FindResource("ToggleSwitchStyle"), HorizontalAlignment = HorizontalAlignment.Left };
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.SetResourceReference(Panel.BackgroundProperty, "AppBackdropBrush"); panel.Children.Add(toggle);
            var window = new Window { Width = 500, Height = 180, Content = panel }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            var indicator = Descendants(toggle).OfType<Views.Controls.SwitchIndicator>().Single();
            var provider = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
            try
            {
                foreach (var x in new[] { 2d, 11d, 25d, 34d })
                {
                    var point = indicator.TranslatePoint(new Point(x, 10), toggle);
                    var hit = (DependencyObject)toggle.InputHitTest(point);
                    while (hit is not null && !ReferenceEquals(hit, toggle)) hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(toggle, hit);
                }
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); Views.Controls.SwitchIndicator.AnimationEnabledOverride = true;
                    Assert.Equal(0, indicator.Progress); Render(panel, "switch-off-" + theme);
                    var bitmap = new RenderTargetBitmap(36, 20, 96, 96, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(indicator), null, new Rect(0, 0, 36, 20));
                    bitmap.Render(visual);
                    var pixels = new byte[36 * 20 * 4]; bitmap.CopyPixels(pixels, 144, 0);
                    Color Pixel(int x, int y) { var i = (y * 36 + x) * 4; return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]); }
                    Assert.Equal(((SolidColorBrush)indicator.OffBrush).Color, Pixel(4, 10));
                    Assert.Equal(((SolidColorBrush)indicator.ThumbOffBrush).Color, Pixel(11, 10));
                    provider.Toggle(); DlssFeedbackTests.PumpUntil(() => indicator.Progress is > 0 and < 1);
                    Render(panel, "switch-opening-" + theme); DlssFeedbackTests.PumpUntil(() => indicator.Progress == 1);
                    Assert.True(toggle.IsChecked); Render(panel, "switch-on-" + theme);
                    provider.Toggle(); DlssFeedbackTests.PumpUntil(() => indicator.Progress is > 0 and < 1);
                    var before = indicator.Progress; provider.Toggle(); Assert.InRange(Math.Abs(indicator.Progress - before), 0, 0.05);
                    DlssFeedbackTests.PumpUntil(() => indicator.Progress == 1);
                    provider.Toggle(); DlssFeedbackTests.PumpUntil(() => indicator.Progress == 0); Assert.False(toggle.IsChecked);
                    Views.Controls.SwitchIndicator.AnimationEnabledOverride = null; UiPerformance.LowSpec = true;
                    provider.Toggle(); Assert.Equal(1, indicator.Progress); provider.Toggle(); Assert.Equal(0, indicator.Progress);
                }
            }
            finally { Views.Controls.SwitchIndicator.AnimationEnabledOverride = null; window.Close(); }
        });
    }

    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static void RunUi(Action<Application, string> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? app = null; var oldGame = AppState.GamePath; var oldItems = AppState.Items; var oldSpec = UiPerformance.LowSpec;
            try
            {
                var root = Path.Combine(Output(), "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                UserDataPaths.RootOverride = root; UiPerformance.LowSpec = true;
                AppState.GamePath = Path.Combine(root, "FixtureGame.exe"); File.WriteAllText(AppState.GamePath, "fixture; never executed");
                PerformanceSessionStore.Save(SessionConditionTests.Example());
                PerformanceSessionStore.Save(SessionConditionTests.Example("Comparison"));
                DisplayQualityService.ApiOverride = () => Api;
                DisplayQualityService.BackupDirOverride = Path.Combine(root, "display-quality"); DisplayTopology.Override = () => [];
                AppState.Items = ItemCatalog.All.Where(d => d.Available).Select(d => new OptimizationItem(d.Id, d.DisplayName, d.DisplayDescription,
                    d.DisplaySideEffect, d.Admin, d.Reboot, false, "test original", false, d.Group)).ToList();
                var compiledApp = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                compiledApp.InitializeComponent(); app = compiledApp;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                LangService.Apply(LangService.ZhCn); DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
                SettingsService.Current.HotkeyEnabled = false;
                test(app, root);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                app?.Shutdown(); DisplayQualityService.ApiOverride = null; DisplayQualityService.BackupDirOverride = null;
                DisplayTopology.Override = null; DialogService.PrepareWindowForTest = null; UserDataPaths.RootOverride = null;
                AppState.GamePath = oldGame; AppState.Items = oldItems; UiPerformance.LowSpec = oldSpec;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Readability / frame UI check did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static readonly DisplayQualityTests.FakeNvdrsApi Api = new();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static string Output()
    {
        var path = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "readability-frames", "ui"); Directory.CreateDirectory(path); return path;
    }
    private static void Capture(MainWindow window, string name, List<object> report)
    {
        window.UpdateLayout();
        var texts = Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).ToArray();
        Assert.DoesNotContain(texts, t => t.FontSize < 13);
        report.Add(new { page = name, count = texts.Length, minFontDip = texts.Select(t => t.FontSize).DefaultIfEmpty(12).Min(),
            below13 = texts.Where(t => t.FontSize < 13).Select(t => new { t.Text, t.FontSize }).ToArray() });
        Render((FrameworkElement)window.FindName("ChromeRoot"), name);
    }
    private static void Render(FrameworkElement element, string name)
    {
        if (name.EndsWith("-light") || name.EndsWith("-dark"))
        {
            var expected = (Color)ColorConverter.ConvertFromString(name.EndsWith("-light") ? "#F7F9FC" : "#0C1015")!;
            DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)Application.Current.FindResource("AppBackdropBrush")).Color == expected);
        }
        var frame = new DispatcherFrame();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(visual);
        using var stream = File.Create(Path.Combine(Output(), name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
