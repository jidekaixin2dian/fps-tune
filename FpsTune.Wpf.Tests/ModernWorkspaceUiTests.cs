using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using FpsTune.Wpf.Views.Controls;
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
                var ids = AppState.Items.Where(i => !i.RequiresAdmin).Take(2).Select(i => i.Id).ToArray();
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
                // 「显示已选」按钮已删除：它就是「仅看已选」这个筛选值，两个入口并排会让人以为功能不同。
                ((ComboBox)page.FindName("ScopeFilter")).SelectedIndex = 2;
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

    [Fact]
    public void Partial_apply_lists_every_item_instead_of_dumping_the_raw_result()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            // 现场问题：只要有一项失败，整块结果就变成「执行失败 + 原始 JSON」，用户只看到一屏数据。
            AppState.Items = ItemCatalog.All.Where(d => d.Id is "game-mode" or "gpu-pref")
                .Select(d => new OptimizationItem(d.Id, d.DisplayName, d.DisplayDescription, d.DisplaySideEffect,
                    d.Admin, d.Reboot, false, "test original", false, d.Group)).ToList();
            var gpuName = ItemCatalog.All.Single(i => i.Id == "gpu-pref").DisplayName;
            var gameName = ItemCatalog.All.Single(i => i.Id == "game-mode").DisplayName;
            var page = new OptimizeView();
            var window = new Window { Width = 1180, Height = 720, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
            var accept = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            accept.Tick += (_, _) =>
            {
                if (app.Windows.OfType<OptimizationReviewWindow>().FirstOrDefault(w => w.IsVisible) is { } review)
                    ((Button)review.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            try
            {
                page.RefreshDetectionOverride = () => Task.FromResult(true);
                page.ApplyItemsOverride = (_, _) => Task.FromResult(new RunResult(1, JsonSerializer.Serialize(new
                {
                    results = new object[]
                    {
                        new { id = "game-mode", name = gameName, ok = true, skipped = false, message = "已开启游戏模式" },
                        new { id = "gpu-pref", name = gpuName, ok = false, skipped = false, message = Str.T("Str.OptFailNeedAdmin") }
                    },
                    summary = "1 成功、1 失败、0 跳过",
                    backupFile = Path.Combine(root, "backup", "v2-backup-test.json"),
                    reboot = Array.Empty<string>()
                }), ""));
                page.SelectForReview(["game-mode", "gpu-pref"]);
                var consent = (CheckBox)page.FindName("ConsentCheck"); consent.IsChecked = true;
                var apply = (Button)page.FindName("ApplyButton");
                accept.Start();
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => apply.IsEnabled);

                var doc = (System.Windows.Documents.FlowDocument)page.FindName("OutputDoc");
                var text = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd).Text;
                Assert.Contains(Str.T("Str.ApplyPartialHead"), text);
                Assert.Contains(Str.T("Str.OptApplyCounts", 1, 1, 0), text);
                Assert.Contains(gameName, text);                       // 成功的项照样列出来
                Assert.Contains(Str.T("Str.OptFailNeedAdmin"), text);   // 失败给的是能照做的原因
                Assert.Contains(Str.T("Str.OptApplyNextAdmin", 1, gpuName), text);
                Assert.DoesNotContain(Str.T("Str.ExecFailedSp"), text); // 不再是整体失败
                Assert.DoesNotContain("\"results\"", text);             // 不再把原始 JSON 灌进界面
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(page, "apply-partial-" + theme); }
            }
            finally { accept.Stop(); window.Close(); DialogService.PrepareWindowForTest = null; }
        });
    }

    [Fact]
    public void Administrator_items_are_reported_before_the_review_dialog_instead_of_after_it()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            var admin = ItemCatalog.All.Where(d => d.Admin && d.Available).Take(2).ToArray();
            AppState.Items = admin.Select(d => new OptimizationItem(d.Id, d.DisplayName, d.DisplayDescription,
                d.DisplaySideEffect, d.Admin, d.Reboot, false, "test original", false, d.Group)).ToList();
            var page = new OptimizeView();
            var window = new Window { Width = 1180, Height = 720, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            DialogService.PrepareWindowForTest = DlssFeedbackTests.PrepareBackgroundWindow;
            AdminHelper.IsAdministratorOverride = () => false;
            var applied = 0; var reviewOpened = false; string? dialogBody = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                if (app.Windows.OfType<OptimizationReviewWindow>().Any(w => w.IsVisible)) reviewOpened = true;
                if (app.Windows.OfType<AppDialogWindow>().FirstOrDefault(w => w.IsVisible) is { } dialog)
                {
                    var body = (TextBlock)dialog.FindName("MessageTextBlock");
                    dialogBody ??= new System.Windows.Documents.TextRange(body.ContentStart, body.ContentEnd).Text;
                    ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            try
            {
                page.RefreshDetectionOverride = () => Task.FromResult(true);
                page.ApplyItemsOverride = (_, _) => { applied++; return Task.FromResult(new RunResult(0, "{}", "")); };
                page.SelectForReview(admin.Select(d => d.Id));
                ((CheckBox)page.FindName("ConsentCheck")).IsChecked = true;
                var apply = (Button)page.FindName("ApplyButton");
                timer.Start();
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                timer.Stop();

                // 关键：审阅窗根本没打开，用户不是"审完再被拦下"。
                Assert.False(reviewOpened);
                Assert.Equal(0, applied);
                Assert.True(apply.IsEnabled);
                Assert.NotNull(dialogBody);
                Assert.Contains(Str.T("Str.NeedsAdmin"), dialogBody);
                Assert.Contains(admin[0].DisplayName, dialogBody);
                foreach (var theme in new[] { "light", "dark" }) { ThemeManager.SetMode(theme); Render(page, "admin-precheck-" + theme); }
            }
            finally { timer.Stop(); AdminHelper.IsAdministratorOverride = null; window.Close(); DialogService.PrepareWindowForTest = null; }
        });
    }

    [Fact]
    public void Update_dialog_downloads_with_progress_cancels_retries_and_only_exits_after_worker_handoff()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            var mode = "cancel"; var downloads = 0; var exits = 0;
            UpdateRecovery.InstallDirectoryOverride = Path.Combine(root, "app");
            UpdateRecovery.ReleaseOverride = (_, _) => Task.FromResult<UpdateInfo?>(null);
            UpdateRecovery.PackageOverride = async (info, progress, token) =>
            {
                downloads++; progress?.Invoke(42);
                if (mode == "cancel") await Task.Delay(Timeout.Infinite, token);
                var bytes = new byte[] { 1, 2, 3, 4 };
                var package = new CachedUpdatePackage(Guid.NewGuid().ToString("N"), info.Version, info.ReleaseTag!,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                var path = UpdateRecovery.PackagePath(package); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes); return package;
            };
            UpdateRecovery.WorkerOverride = (_, _) => throw new IOException("controlled handoff failure");
            var window = new UpdateAvailableWindow(UpdateRecoveryTests.Info("v0.3.3")) { Width = 540, Height = 460,
                FinishInstallation = () => exits++ };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Click("InstallButton"); Click("InstallButton");
                Assert.True(window.IsRunning);
                DlssFeedbackTests.PumpUntil(() => downloads == 1); // Cache preparation now runs off the UI thread.
                Assert.Equal(1, downloads);
                Assert.Equal(42, ((ProgressBar)window.FindName("DownloadProgress")).Value);
                Assert.False(((Button)window.FindName("DownloadPageButton")).IsEnabled);
                foreach (var theme in new[] { "light", "dark" })
                { ThemeManager.SetMode(theme); Render((FrameworkElement)window.Content, "automatic-update-" + theme); }
                Click("CancelDownloadButton"); DlssFeedbackTests.PumpUntil(() => !window.IsRunning);
                Assert.Equal(0, exits); Assert.Null(UpdateRecovery.Recent());
                Assert.Equal(Str.T("Str.UpdateCancelled"), ((TextBlock)window.FindName("StatusText")).Text);
                mode = "failure"; Click("InstallButton"); DlssFeedbackTests.PumpUntil(() => !window.IsRunning);
                Assert.Equal(0, exits); Assert.True(window.IsVisible);
                Assert.Contains("controlled handoff failure", ((TextBlock)window.FindName("StatusText")).Text);
                Assert.NotNull(UpdateRecovery.ProtectedCopy(UpdateRecovery.Recent()!));
                UpdateRecovery.WorkerOverride = (_, _) => Task.CompletedTask;
                Click("InstallButton"); DlssFeedbackTests.PumpUntil(() => exits == 1);
                Assert.Equal("upgrade", UpdateRecovery.Recent()!.Mode);
            }
            finally
            {
                window.Close(); UpdateRecovery.InstallDirectoryOverride = null; UpdateRecovery.ReleaseOverride = null;
                UpdateRecovery.PackageOverride = null; UpdateRecovery.WorkerOverride = null;
            }
        });
    }

    [Fact]
    public void Color_preview_loads_local_screenshot_updates_from_parameters_and_keeps_comparison_split()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            var panel = new ScreenColorPanel();
            var surface = new Border { Padding = new Thickness(20), Child = panel };
            surface.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
            var window = new Window { Width = 1180, Height = 720, Content = new ScrollViewer { Content = surface } };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var preview = (FpsTune.Wpf.Views.Controls.ColorPreviewControl)panel.FindName("ColorPreview");
                var scene = (ComboBox)preview.FindName("ScreenshotCombo");
                DlssFeedbackTests.PumpUntil(() => ((Image)preview.FindName("BeforeImage")).Source is not null);
                var delta = ((Image)preview.FindName("BeforeImage")).Source;
                Assert.Equal(Str.T("Str.ColorPreviewDelta"), ((FileNameLabel)preview.FindName("ImageNameText")).Text);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)preview.FindName("EmptyHint")).Visibility);
                scene.SelectedIndex = 1;
                DlssFeedbackTests.PumpUntil(() => !ReferenceEquals(delta, ((Image)preview.FindName("BeforeImage")).Source));
                Assert.Equal(Str.T("Str.ColorPreviewValorant"), ((FileNameLabel)preview.FindName("ImageNameText")).Text);
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "comparison.png");
                using (var file = File.Create(path))
                { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ScreenColorPreview.Sample())); encoder.Save(file); }
                var loading = preview.LoadImageAsync(path); DlssFeedbackTests.PumpUntil(() => loading.IsCompleted); loading.GetAwaiter().GetResult();
                Assert.Equal("comparison.png", ((FileNameLabel)preview.FindName("ImageNameText")).Text);
                Assert.Equal(2, scene.SelectedIndex);
                var original = ((Image)preview.FindName("BeforeImage")).Source;
                var before = ((Image)preview.FindName("AfterImage")).Source;
                var image = (Image)preview.FindName("AfterImage");
                byte[] RenderImage()
                {
                    image.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)image.ActualWidth, (int)image.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(image);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
                }
                var neutralPixels = RenderImage();
                var render = preview.RenderedRevision;
                ((ComboBox)panel.FindName("ColorPresetCombo")).SelectedIndex = 4;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedGeneration == preview.CurrentGeneration);
                // 预览按写入同款曲线重算像素：源图不变，合成结果必须变。
                Assert.NotSame(before, image.Source);
                Assert.False(neutralPixels.SequenceEqual(RenderImage()));
                Assert.Same(original, ((Image)preview.FindName("BeforeImage")).Source);
                ((ComboBox)panel.FindName("ColorModeCombo")).SelectedIndex = 1;
                var parameterGrid = (System.Windows.Controls.Primitives.UniformGrid)panel.FindName("ParameterGrid");
                var brightness = ((StackPanel)parameterGrid.Children[0]).Children.OfType<Slider>().Single();
                var brightnessValue = brightness.Value;
                var parameterSliders = parameterGrid.Children.OfType<StackPanel>()
                    .SelectMany(child => child.Children.OfType<Slider>()).ToArray();
                var parameterValues = parameterSliders.Select(slider => slider.Value).ToArray();
                // 新契约（2026-11-03）：选中滤镜＝把该预设的画面参数填进面板（草稿，仍需点「应用」）。
                // 旧契约刻意不动滑杆，把滤镜观感全压给折进 ramp 的 ICC 曲线；结果曲线与画面参数互相抵消，
                // 所有预设预览都一样——正是用户反馈的「所有 ICC 滤镜看着区别不大」。
                render = preview.RenderedRevision;
                for (var i = 0; i < 8; i++) ((ComboBox)panel.FindName("IccPresetCombo")).SelectedIndex = 5 + i % 2;
                Assert.False(parameterValues.SequenceEqual(parameterSliders.Select(slider => slider.Value)),
                    "换 ICC 预设后画面参数应当跟着变（预设就是一套调色）");
                DlssFeedbackTests.PumpUntil(() => preview.RenderedGeneration == preview.CurrentGeneration);
                var gamePixels = RenderImage();
                Assert.Equal(string.Empty, ((TextBlock)preview.FindName("PreviewStatus")).Text); // 通道由单选钮标明
                // 语义已改（2026-11-02）：游戏不读 ICC 关联，但滤镜的 TRC 曲线会折进 gamma ramp，
                // 所以「游戏内效果」换预设必须变——这正是用户反馈「滤镜无效」要修的地方。
                render = preview.RenderedRevision;
                ((ComboBox)panel.FindName("IccPresetCombo")).SelectedIndex = 5;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedGeneration == preview.CurrentGeneration);
                Assert.False(gamePixels.SequenceEqual(RenderImage()));
                // 「色彩管理应用」才是 ICC + gamma：两者各算一次，必须与游戏视图不同。
                render = preview.RenderedRevision;
                ((RadioButton)preview.FindName("ChannelManaged")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedGeneration == preview.CurrentGeneration);
                Assert.Equal(PreviewChannelKind.ColorManaged, preview.Channel);
                var managedPixels = RenderImage();
                Assert.False(gamePixels.SequenceEqual(managedPixels));
                render = preview.RenderedRevision;
                brightness.Value = 10;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedGeneration == preview.CurrentGeneration);
                Assert.False(managedPixels.SequenceEqual(RenderImage()));
                Assert.Equal(string.Empty, ((TextBlock)preview.FindName("PreviewStatus")).Text); // 通道由单选钮标明
                Assert.Equal(1, ((ComboBox)panel.FindName("ColorModeCombo")).SelectedIndex);
                Assert.Equal(5, ((ComboBox)panel.FindName("IccPresetCombo")).SelectedIndex);
                foreach (var index in new[] { 0, 1 })
                {
                    scene.SelectedIndex = index;
                    DlssFeedbackTests.PumpUntil(() => ReferenceEquals(preview.RenderedSource, ((Image)preview.FindName("BeforeImage")).Source)
                        && ((FileNameLabel)preview.FindName("ImageNameText")).Text == Str.T(index == 0 ? "Str.ColorPreviewDelta" : "Str.ColorPreviewValorant"));
                    var active = ((Image)preview.FindName("BeforeImage")).Source;
                    if (index == 0)
                    {
                        var first = (BitmapSource)delta; var second = (BitmapSource)active;
                        Assert.Equal(first.PixelWidth, second.PixelWidth); Assert.Equal(first.PixelHeight, second.PixelHeight); Assert.Equal(first.Format, second.Format);
                        var stride = (first.PixelWidth * first.Format.BitsPerPixel + 7) / 8;
                        var a = new byte[stride * first.PixelHeight]; var b = new byte[a.Length];
                        first.CopyPixels(a, stride, 0); second.CopyPixels(b, stride, 0); Assert.Equal(a, b);
                    }
                    Assert.NotSame(active, image.Source);
                    foreach (var theme in new[] { "light", "dark" })
                    { ThemeManager.SetMode(theme); Render(surface, "game-icc-" + index + "-" + theme); }
                }
                // 关掉 ICC 写入模式后切回「游戏内效果」：合成结果里不再有 ICC，与色彩管理视图不同。
                var managedOnly = RenderImage();
                ((ComboBox)panel.FindName("ColorModeCombo")).SelectedIndex = 0;
                render = preview.RenderedRevision;
                ((RadioButton)preview.FindName("ChannelGame")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedGeneration == preview.CurrentGeneration);
                Assert.Equal(PreviewChannelKind.Game, preview.Channel);
                Assert.False(managedOnly.SequenceEqual(RenderImage()));
                ((Slider)preview.FindName("SplitSlider")).Value = 25;
                var bounds = ((RectangleGeometry)((Grid)preview.FindName("BeforeLayer")).Clip).Rect;
                Assert.Equal(((Grid)preview.FindName("ComparisonSurface")).ActualWidth / 4 + 6, bounds.Width, 2);
                var comparison = (Grid)preview.FindName("ComparisonSurface");
                var source = (BitmapSource)((Image)preview.FindName("BeforeImage")).Source;
                // Viewbox 按 DPI 缩放到像素边界会有不到 0.3% 的舍入，2 位小数足够锁 16:9 语义。
                Assert.Equal((double)source.PixelWidth / source.PixelHeight, comparison.ActualWidth / comparison.ActualHeight, 2);
                preview.UpdateLayout();
                var track = (System.Windows.Controls.Primitives.Track)((Slider)preview.FindName("SplitSlider")).Template.FindName("PART_Track", (Slider)preview.FindName("SplitSlider"));
                Assert.Equal(comparison.ActualHeight, track.Thumb.ActualHeight, 2);
                // Viewbox 会把内部坐标一并缩放，TranslatePoint 不再适合算屏幕像素位置；
                // 改用稳定的 24px 手柄宽度 + 上方 BeforeLayer clip 断言，视觉位置由真机截图复核。
                Assert.Equal(24, track.Thumb.ActualWidth, 2);
                // 参考实现版式下窄窗口把预设列收起（宽度 0），调色区固定在第 3 行；宽屏恢复左列。
                window.Width = 760; window.UpdateLayout();
                Assert.Equal(0d, ((ColumnDefinition)panel.FindName("PresetColumn")).Width.Value, 1);
                window.Width = 2000; window.UpdateLayout();
                Assert.True(((ColumnDefinition)panel.FindName("PresetColumn")).Width.Value > 300);
                Assert.True(panel.ActualWidth <= 1960);
                Assert.Equal((double)source.PixelWidth / source.PixelHeight, comparison.ActualWidth / comparison.ActualHeight, 2);
                Assert.False(((Button)panel.FindName("ColorApplyButton")).IsEnabled);
                foreach (var theme in new[] { "light", "dark" })
                { ThemeManager.SetMode(theme); Render(surface, "color-preview-" + theme); }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Filter_category_is_remembered_for_the_next_visit()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, _) =>
        {
            var settings = SettingsService.Current;
            settings.FilterCategory = "all";
            var first = new FilterCenterPanel();
            var window = new Window { Width = 1180, Height = 800, Content = new ScrollViewer { Content = first } };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                DlssFeedbackTests.PumpUntil(() => first.FindName("CategoryList") is StackPanel chips && chips.Children.Count > 1);
                // 分类是单行（放不下就横向滚动），不再是会折成四行的 chips 墙。
                var list = (StackPanel)first.FindName("CategoryList");
                var soft = list.Children.OfType<RadioButton>().Single(c => (string)c.Tag == "soft");
                soft.IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => SettingsService.Current.FilterCategory == "soft");
                Assert.Equal("soft", first.Category);
                // 切到别的分类会影响下一次渲染的列表内容
                var game = list.Children.OfType<RadioButton>().Single(c => (string)c.Tag == "game");
                game.IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => SettingsService.Current.FilterCategory == "game");
            }
            finally { window.Content = null; window.Close(); }

            // 重新打开滤镜面板：直接落在上次的分类上
            var second = new FilterCenterPanel();
            Assert.Equal("game", second.Category);
            SettingsService.Current.FilterCategory = "all";
        });
    }

    [Fact]
    public void Dpc_panel_renders_admin_gate_and_driver_breakdown_without_crashing()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, _) =>
        {
            SettingsService.Current.ThemeMode = "dark"; ThemeManager.SetMode("dark");
            DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == Color.FromRgb(0x0C, 0x10, 0x15));
            var panel = new DpcLatencyPanel { Width = 760 };
            var window = new Window { Width = 800, Height = 620, Content = panel };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                window.UpdateLayout();
                Assert.False(string.IsNullOrWhiteSpace(((TextBlock)panel.FindName("DpcAdminText")).Text));
                Assert.Equal(Visibility.Collapsed, ((StackPanel)panel.FindName("DpcResultPanel")).Visibility);
                panel.DisplayReport(new DpcLatencyReport(true, DateTimeOffset.Now, 30, 16,
                    42.5, 3.21, 8.4, 0.8, 1280, 240, 0.09, 0.02,
                    [new DpcDriverSample("nvlddmkm.sys", 800, 160, 31.2, 3.21, 5.4, 0.72),
                     new DpcDriverSample("ndis.sys", 400, 80, 9.4, 1.2, 2.8, 0.36)],
                    Str.T("Str.DpcVerdictWatch"), "watch", null));
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, ((StackPanel)panel.FindName("DpcResultPanel")).Visibility);
                Assert.Equal("nvlddmkm.sys", ((IEnumerable<DpcDriverSample>)((ItemsControl)panel.FindName("DpcDriversList")).ItemsSource).First().Module);
                Render(panel, "dpc-panel-dark");
                SettingsService.Current.ThemeMode = "light"; ThemeManager.SetMode("light");
                DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == Color.FromRgb(0xF7, 0xF9, 0xFC));
                window.UpdateLayout(); Render(panel, "dpc-panel-light");
            }
            finally { window.Content = null; window.Close(); }
        });
    }

    [Fact]
    public void Filter_workspace_keeps_preview_visible_and_switches_between_screen_and_per_app_plans()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, _) =>
        {
            SettingsService.Current.ThemeMode = "dark"; ThemeManager.SetMode("dark");
            DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == Color.FromRgb(0x0C, 0x10, 0x15));
            IccAppRuleStore.Invalidate();
            IccAppRuleStore.Save([new FilterAppRule("fixture.exe", "generated:Vivid", 60, true)]);
            var page = new FilterView();
            var window = new Window { Width = 1440, Height = 900, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var color = (ScreenColorPanel)page.FindName("ColorPanel");
                var preview = (FpsTune.Wpf.Views.Controls.ColorPreviewControl)color.FindName("ColorPreview");
                DlssFeedbackTests.PumpUntil(() => preview.IsVisible && ((Image)preview.FindName("BeforeImage")).Source is not null);
                Assert.Null(page.FindName("TabLibrary"));
                Assert.True(((RadioButton)page.FindName("ModeScreen")).IsChecked);
                var filter = (FilterCenterPanel)page.FindName("FilterCenter");
                Assert.Same(filter, ((ContentControl)color.FindName("FilterHost")).Content);
                Assert.True(((Border)color.FindName("PreviewPane")).IsVisible);
                Assert.Equal(Visibility.Visible, ((StackPanel)color.FindName("CurrentModePanel")).Visibility);
                Render(page, "filter-workspace-screen-dark");
                SettingsService.Current.ThemeMode = "light"; ThemeManager.SetMode("light");
                DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == Color.FromRgb(0xF7, 0xF9, 0xFC));
                window.UpdateLayout(); Render(page, "filter-workspace-screen-light");
                SettingsService.Current.ThemeMode = "dark"; ThemeManager.SetMode("dark");
                DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == Color.FromRgb(0x0C, 0x10, 0x15));
                window.UpdateLayout();

                ((RadioButton)page.FindName("ModePerApp")).IsChecked = true;
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, ((StackPanel)color.FindName("CurrentModePanel")).Visibility);
                Assert.Equal(Visibility.Visible, ((Border)color.FindName("PerAppCard")).Visibility);
                var perApp = (PerAppFilterPanel)page.FindName("PerAppCenter");
                var rules = (ListBox)perApp.FindName("RuleList");
                DlssFeedbackTests.PumpUntil(() => rules.Items.Count == 1);
                // 规则行按 参考实现收成「exe 名 + 徽章 + 四个动作」，不再有内嵌编辑表单。
                var row = rules.Items[0]!;
                Assert.Equal("fixture.exe", (string)row.GetType().GetProperty("ExeName")!.GetValue(row)!);
                Assert.NotNull(perApp.FindName("DaemonToggleButton"));
                Assert.NotNull(perApp.FindName("FallbackCheck"));
                // 独立程序滤镜页不放截图对比：规则不该被一张大图挤到屏幕外。
                Assert.Equal(Visibility.Collapsed, ((Border)color.FindName("PreviewPane")).Visibility);
                Render(page, "filter-workspace-perapp-dark");
                // 浅色主题 + 窄窗口（接近最小窗口）也要在位：这两张是给真机核对用的离屏渲染证据。
                SettingsService.Current.ThemeMode = "light"; ThemeManager.SetMode("light");
                DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == Color.FromRgb(0xF7, 0xF9, 0xFC));
                window.UpdateLayout(); Render(page, "filter-workspace-perapp-light");
                window.Width = 900; window.Height = 720; window.UpdateLayout();
                Render(page, "filter-workspace-perapp-narrow");
                window.Width = 1440; window.Height = 900;
                SettingsService.Current.ThemeMode = "dark"; ThemeManager.SetMode("dark");
                DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == Color.FromRgb(0x0C, 0x10, 0x15));
                window.UpdateLayout();

                ((RadioButton)page.FindName("ModeScreen")).IsChecked = true;
                Assert.Equal(Visibility.Visible, ((StackPanel)color.FindName("CurrentModePanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Border)color.FindName("PerAppCard")).Visibility);
                Assert.Equal(Visibility.Visible, ((Border)color.FindName("PreviewPane")).Visibility);
            }
            finally { window.Close(); IccAppRuleStore.Invalidate(); }
        });
    }

    [Fact]
    public void Startup_splash_is_topmost_so_the_loading_window_never_falls_behind_the_shell()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, _) =>
        {
            var splash = new SplashWindow();
            try
            {
                app.MainWindow = splash; DlssFeedbackTests.PrepareBackgroundWindow(splash); splash.Show();
                Assert.True(splash.Topmost);
                Assert.False(splash.ShowInTaskbar);
            }
            finally { splash.Close(); }
        });
    }

    [Fact]
    public void Split_handle_covers_the_comparison_surface_so_it_can_be_dragged()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, root) =>
        {
            var preview = new FpsTune.Wpf.Views.Controls.ColorPreviewControl();
            var window = new Window { Width = 1100, Height = 760, Content = preview };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var path = Path.Combine(root, "split.png");
                using (var file = File.Create(path))
                { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ScreenColorPreview.Sample())); encoder.Save(file); }
                var load = preview.LoadImageAsync(path); DlssFeedbackTests.PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
                window.UpdateLayout();
                var slider = (Slider)preview.FindName("SplitSlider");
                var surface = (Grid)preview.FindName("ComparisonSurface");
                Assert.True(slider.ActualWidth > surface.ActualWidth * 0.9,
                    $"slider={slider.ActualWidth:0.##}x{slider.ActualHeight:0.##} surface={surface.ActualWidth:0.##}x{surface.ActualHeight:0.##}");
                Assert.True(slider.ActualHeight > surface.ActualHeight * 0.9,
                    $"slider={slider.ActualWidth:0.##}x{slider.ActualHeight:0.##} surface={surface.ActualWidth:0.##}x{surface.ActualHeight:0.##}");
                // 图片占满剩余高度，说明文字永远是 Auto 行：两条提示都必须完整落在控件内。
                var viewbox = (Viewbox)preview.FindName("ComparisonViewbox");
                Assert.True(viewbox.ActualHeight > 300, $"viewbox={viewbox.ActualWidth:0.##}x{viewbox.ActualHeight:0.##}");
                var status = (TextBlock)preview.FindName("PreviewStatus");
                var statusBottom = status.TranslatePoint(new Point(0, status.ActualHeight), preview).Y;
                Assert.True(statusBottom <= preview.ActualHeight + 0.5,
                    $"statusBottom={statusBottom:0.##} previewHeight={preview.ActualHeight:0.##}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Filter_selection_clears_with_the_panel_ICC_and_can_be_selected_again()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, _) =>
        {
            IccAppRuleStore.Invalidate();
            var page = new FilterView();
            var window = new Window { Width = 1440, Height = 900, Content = page };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var color = (ScreenColorPanel)page.FindName("ColorPanel");
                var filter = (FilterCenterPanel)page.FindName("FilterCenter");
                // 滤镜列表按 参考实现一条一行，整行可点（不再是两列 chips）。
                var chips = (StackPanel)filter.FindName("PresetList");
                DlssFeedbackTests.PumpUntil(() => chips.Children.OfType<RadioButton>().Any());
                var first = chips.Children.OfType<RadioButton>().First();
                first.IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => ((ComboBox)color.FindName("ColorModeCombo")).SelectedIndex == 1);
                // 面板丢了 ICC（还原原始后的系统状态）时列表选中态跟着清空，避免亮着却点不动。
                filter.ClearSelection();
                Assert.DoesNotContain(chips.Children.OfType<RadioButton>(), r => r.IsChecked == true);
                ((ComboBox)color.FindName("ColorModeCombo")).SelectedIndex = 0;
                var again = chips.Children.OfType<RadioButton>().First();
                again.IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => ((ComboBox)color.FindName("ColorModeCombo")).SelectedIndex == 1);
                Assert.True(again.IsChecked);
            }
            finally { window.Close(); IccAppRuleStore.Invalidate(); }
        });
    }

    [Fact]
    public void Per_app_panel_renders_the_three_1u_sections_and_rule_actions_write_through()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, _) =>
        {
            IccAppRuleStore.Invalidate();
            var settings = SettingsService.Current;
            var oldEnabled = settings.FilterWatcherEnabled;
            settings.FilterWatcherEnabled = false;
            IccAppRuleStore.Save([new FilterAppRule("fixture.exe", "generated:Vivid", 60, true, Auto: true)]);
            var panel = new PerAppFilterPanel(null);
            var window = new Window { Width = 1180, Height = 900, Content = new ScrollViewer { Content = panel } };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var list = (ListBox)panel.FindName("RuleList");
                DlssFeedbackTests.PumpUntil(() => list.Items.Count == 1);
                window.UpdateLayout(); panel.UpdateLayout();
                // 参考实现三块都在：顶部扫描/添加/开关、运行状态行（三复选框 + 停止）、底部全自动收录与范围
                Assert.NotNull(((Button)panel.FindName("ScanButton")).Content);
                Assert.NotNull(((Button)panel.FindName("AddExeButton")).Content);
                Assert.Equal(3, new[] { "KeepAfterExitCheck", "NotifyCheck", "RestoreOnLeaveCheck" }
                    .Count(name => panel.FindName(name) is CheckBox));
                Assert.NotNull((Button)panel.FindName("DaemonToggleButton"));
                Assert.Contains("1", ((TextBlock)panel.FindName("AutoTitle")).Text);
                Assert.NotNull((RadioButton)panel.FindName("ScopeFullscreenRadio"));
                Assert.NotNull((RadioButton)panel.FindName("ScopeGameDirRadio"));
                Assert.NotNull((RadioButton)panel.FindName("ScopeAnyRadio"));
                Render(panel, "perapp-panel-dark");

                // 转为手动：自动徽章消失，这条规则从此自己说了算。
                DlssFeedbackTests.PumpUntil(() => Descendants(list).OfType<Button>().Any(b => b.Tag is FilterAppRule));
                ButtonAction(list, Str.T("Str.FilterAppsToManual"));
                DlssFeedbackTests.PumpUntil(() => !IccAppRuleStore.Rules.Single().Auto);
                Assert.False(IccAppRuleStore.Rules.Single().Auto);

                // 重置为桌面设置：清掉这条规则自己的滤镜与参数，变成「跟随桌面」。
                DlssFeedbackTests.PumpUntil(() => Descendants(list).OfType<Button>().Any(b => Equals(b.Content, Str.T("Str.FilterAppsResetDesktop"))));
                ButtonAction(list, Str.T("Str.FilterAppsResetDesktop"));
                DlssFeedbackTests.PumpUntil(() => !IccAppRuleStore.Rules.Single().HasEffect);
                Assert.Equal("", IccAppRuleStore.Rules.Single().PresetFileName);

                // 顶部开关：写回设置并让守护重估。
                var toggle = (CheckBox)panel.FindName("WatcherCheck");
                toggle.IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => SettingsService.Current.FilterWatcherEnabled);
                Assert.True(SettingsService.Current.FilterWatcherEnabled);
            }
            finally
            {
                settings.FilterWatcherEnabled = oldEnabled;
                window.Content = null; window.Close(); IccAppRuleStore.Invalidate();
            }
        });
    }

    private static void ButtonAction(DependencyObject list, string content)
    {
        var button = Descendants(list).OfType<Button>().First(b => Equals(b.Content, content));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [Fact]
    public void Apply_bar_targets_a_rule_store_the_auto_template_and_saves_schemes_without_touching_the_screen()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ModernWorkspaceUiTests)); return; }
        RunUi((app, _) =>
        {
            IccAppRuleStore.Invalidate(); FilterSchemeStore.Invalidate();
            IccAppRuleStore.Save([new FilterAppRule("cs2.exe", "generated:Warm", 20, false, Auto: true)]);
            var panel = new ScreenColorPanel();
            var window = new Window { Width = 1180, Height = 900, Content = new ScrollViewer { Content = panel } };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                window.UpdateLayout(); panel.UpdateLayout();
                var targets = (ComboBox)panel.FindName("ApplyTargetCombo");
                // 桌面 + 全部游戏 + 每条规则一条，与参考实现的「应用到」一致。
                Assert.Equal(3, targets.Items.Count);
                Assert.Equal("desktop", panel.ApplyTargetKey);
                Assert.Contains(targets.Items.Cast<object>(),
                    item => (string)item.GetType().GetProperty("Label")!.GetValue(item)! == Str.T("Str.ColorApplyTargetRule", "cs2.exe"));

                // 选「进游戏时 · cs2.exe」：应用目标切过去，面板填上这条规则的滤镜（草稿）。
                targets.SelectedItem = targets.Items.Cast<object>().Last();
                window.UpdateLayout();
                Assert.StartsWith("rule:", panel.ApplyTargetKey);

                // 存为方案：把当前滤镜+参数存下来，方案下拉立刻能看到。
                panel.SelectGeneratedPreset(IccFilterPreset.Vivid);
                window.UpdateLayout();
                ((Button)panel.FindName("SaveSchemeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var schemes = FilterSchemeStore.Schemes;
                Assert.Single(schemes);
                Assert.True(File.Exists(Path.Combine(UserDataPaths.Root, "game-filters", "schemes.json")));
                var schemeCombo = (ComboBox)panel.FindName("SchemeCombo");
                Assert.Single(schemeCombo.Items);

                // 应用到规则：写进规则（自动规则＝改全自动模板），不写屏幕。
                var before = IccAppRuleStore.Rules.Single();
                ((Button)panel.FindName("ColorApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => FilterSchemeStore.AutoTemplate is not null);
                var template = FilterSchemeStore.AutoTemplate!;
                Assert.Equal(GameFilterLibrary.GeneratedPrefix + nameof(IccFilterPreset.Vivid), template.PresetFileName);
                Assert.True(IccAppRuleStore.Rules.Single().Auto);
                Assert.Equal(before.Key, IccAppRuleStore.Rules.Single().Key);

                // 手动规则：应用时只改这一条，模板不动。
                IccAppRuleStore.Save([IccAppRuleStore.Rules.Single() with { Auto = false }]);
                panel.RefreshApplyTargets();
                ((Button)panel.FindName("ColorApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => IccAppRuleStore.Rules.Single().PresetFileName
                    == GameFilterLibrary.GeneratedPrefix + nameof(IccFilterPreset.Vivid));
                Assert.False(IccAppRuleStore.Rules.Single().Auto);

                // 内置 .icc 名字的规则（老规则就是这么存的）：编辑调色后存方案，存进去的必须是原名，
                // 不是物化用的临时路径（那条路径重启就没了）。
                var builtin = GameFilterLibrary.Presets
                    .First(p => p.Builtin && !p.FileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal)).FileName;
                IccAppRuleStore.Save([new FilterAppRule("cs2.exe", builtin, 55, false)]);
                panel.RefreshApplyTargets();
                panel.SetApplyTarget(IccAppRuleStore.Rules.Single().Key);
                window.UpdateLayout();
                ((Button)panel.FindName("SaveSchemeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(FilterSchemeStore.Schemes, scheme => scheme.PresetFileName == builtin);
                Render(panel, "apply-bar-targets");
            }
            finally
            {
                window.Content = null; window.Close();
                IccAppRuleStore.Invalidate(); FilterSchemeStore.Invalidate();
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
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
        // 页面与卡片的主题色本身带透明度，单独渲染子树时背后没有主窗口底色。
        // 截图前临时铺上当前主题底色，避免透明区域在查看器里被误判成浅色界面。
        var background = element.TryFindResource("AppBackdropBrush") as Brush ?? Brushes.White;
        var control = element as Control;
        var previous = control?.Background;
        if (control is not null) control.Background = background;
        try
        {
            element.UpdateLayout();
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bitmap.Render(drawing);
        }
        finally { if (control is not null) control.Background = previous; }
        using var stream = File.Create(Path.Combine(Output(), name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
