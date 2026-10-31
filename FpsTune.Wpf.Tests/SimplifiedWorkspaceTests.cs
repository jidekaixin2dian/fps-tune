using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class SimplifiedWorkspaceTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Recommendations_exclude_troubleshooting_tweaks_and_remain_in_the_common_catalog()
    {
        Assert.Equal(new[] { "dvr-off", "game-mode", "gpu-pref", "transparency-off" }, OptimizationCatalog.ResolvePreset("balanced").Order());
        foreach (var preset in new[] { "balanced", "safe-only" })
            Assert.All(OptimizationCatalog.ResolvePreset(preset), id => Assert.True(ItemCatalog.All.Single(i => i.Id == id).Common));
        Assert.All(ItemCatalog.All.Where(i => i.Default), i => Assert.Contains(i.Id, OptimizationCatalog.ResolvePreset("balanced")));
        Assert.All(new[] { "fso-off", "mpo-off", "mmcss-games" }, id => Assert.Contains(id, OptimizationCatalog.ResolvePreset("full")));
    }

    [Fact]
    public void Unified_driver_editor_supports_other_games_cap_only_preserves_sync_and_busy_stale_targets_are_rejected()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(SimplifiedWorkspaceTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var path = Path.Combine(root, "other-fps.exe"); File.WriteAllText(path, "not executable");
            StateStore.AddGame(path); AppState.GamePath = path;
            var api = new DisplayQualityTests.FakeNvdrsApi(); DisplayQualityService.ApiOverride = () => api;
            var profile = api.AddProfile("other-fps.exe", "other-fps.exe");
            profile.Settings[DisplayQualityService.VSyncModeId] = 0x47814940u;
            profile.Settings[DisplayQualityService.FrameRateLimitId] = 165;
            var page = new OptimizeView(); page.SetResourceReference(Control.BackgroundProperty, "AppBackdropBrush"); page.ReloadFromState();
            var window = new Window { Width = 1000, Height = 650, Content = page }; app.MainWindow = window;
            DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            var driver = (GameDriverSettings)page.FindName("DriverSettings");
            T Named<T>(string name) => (T)driver.FindName(name);
            var previousBusy = GameContextService.IsBusyProvider;
            try
            {
                Assert.Null(page.FindName("OneClickButton"));
                ((RadioButton)page.FindName("DriverTab")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => Named<Button>("ApplyDriverButton").IsEnabled);
                Assert.Equal(0, Named<ComboBox>("SyncBox").SelectedIndex);
                var noSelection = driver.ChangeDriverAsync(false); DlssFeedbackTests.PumpUntil(() => noSelection.IsCompleted); noSelection.GetAwaiter().GetResult();
                Assert.Equal(0, api.SaveCount);
                Assert.Equal(Str.T("Str.DriverChooseAction"), Named<TextBlock>("DriverStatus").Text);
                Named<CheckBox>("ForegroundCheck").IsChecked = true;
                Named<TextBox>("ForegroundBox").Text = "300";
                DialogService.PrepareWindowForTest = dialog =>
                {
                    DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => dialog.DialogResult = true);
                };
                var apply = driver.ChangeDriverAsync(false); DlssFeedbackTests.PumpUntil(() => apply.IsCompleted); apply.GetAwaiter().GetResult();
                Assert.Equal(1, api.SaveCount); Assert.Equal(300u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
                Assert.Equal(0x47814940u, profile.Settings[DisplayQualityService.VSyncModeId]);
                Assert.False(profile.Settings.ContainsKey(DisplayQualityService.PowerModeId));
                var restore = driver.ChangeDriverAsync(true); DlssFeedbackTests.PumpUntil(() => restore.IsCompleted); restore.GetAwaiter().GetResult();
                Assert.Equal(165u, profile.Settings[DisplayQualityService.FrameRateLimitId]);
                Assert.Equal(0x47814940u, profile.Settings[DisplayQualityService.VSyncModeId]);
                var saves = api.SaveCount;
                GameContextService.IsBusyProvider = () => true;
                var busy = driver.ChangeDriverAsync(false); DlssFeedbackTests.PumpUntil(() => busy.IsCompleted); busy.GetAwaiter().GetResult();
                Assert.Equal(saves, api.SaveCount);
                GameContextService.IsBusyProvider = previousBusy;
                AppState.GamePath = Path.Combine(root, "changed.exe");
                var stale = driver.ChangeDriverAsync(false); DlssFeedbackTests.PumpUntil(() => stale.IsCompleted); stale.GetAwaiter().GetResult();
                Assert.Equal(saves, api.SaveCount);
                Assert.Equal(Str.T("Str.PlanGameChanged"), Named<TextBlock>("DriverStatus").Text);
                AppState.GamePath = path;
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); Render(window, "driver-" + theme);
                    var scroll = (ScrollViewer)page.FindName("DriverScroll"); scroll.ScrollToEnd(); Render(window, "driver-bottom-" + theme);
                    var applyAt = Named<Button>("ApplyDriverButton").TranslatePoint(new Point(), scroll);
                    Assert.InRange(applyAt.Y, 0, scroll.ActualHeight - Named<Button>("ApplyDriverButton").ActualHeight + 1);
                    scroll.ScrollToHome();
                    ((RadioButton)page.FindName("SystemTab")).IsChecked = true; Render(window, "common-" + theme);
                    ((CheckBox)page.FindName("ShowAdvancedItems")).IsChecked = true;
                    Assert.Contains(page.ItemsView.Cast<OptimizationItemViewModel>(), i => i.Id == "mpo-off");
                    Render(window, "advanced-" + theme);
                    ((CheckBox)page.FindName("ShowAdvancedItems")).IsChecked = false;
                    ((RadioButton)page.FindName("DriverTab")).IsChecked = true;
                }
                Assert.Equal(saves, api.SaveCount); // Navigation and rendering do not apply settings.
            }
            finally { GameContextService.IsBusyProvider = previousBusy; window.Close(); }
        });
    }

    private static void Render(Window window, string name)
    {
        var readyAt = DateTime.UtcNow.AddMilliseconds(280);
        DlssFeedbackTests.PumpUntil(() => DateTime.UtcNow >= readyAt);
        window.UpdateLayout(); var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "simplify037", "ui"); Directory.CreateDirectory(output);
        using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
    }
}
