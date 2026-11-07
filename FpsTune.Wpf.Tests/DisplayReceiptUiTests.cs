using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 显示与驱动页的真实渲染与交互：逐项回执面板（8 项写进去 / 1 项驱动不支持 / 只重试失败项）、
/// 高级参数展开态（三态标记 + 改写）、显卡型号预设的「已是当前型号」禁用。
/// 渲染图落到 work/ref-align-297/shots-display。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class DisplayReceiptUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private const string Exe = "FpsTune-DisplayUi-test.exe";
    private const string IdentityInstance = @"PCI\VEN_10DE&DEV_2F18&SUBSYS_000010DE&REV_A1\4&1a2b3c&0&0008";

    private static string Output()
    {
        // 目录不存在就自建：渲染证据不依赖任何人先手工建目录。
        var path = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "ref-align-297", "shots-display");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Receipt_panel_advanced_list_and_current_model_preset_render_in_both_themes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(DisplayReceiptUiTests)); return; }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "fpstune-display-receipt-ui-" + Guid.NewGuid().ToString("N"));
            var api = new DisplayQualityTests.FakeNvdrsApi();
            App? app = null; Window? owner = null; Window? driverHost = null;
            try
            {
                Directory.CreateDirectory(root);
                var fixture = Path.Combine(root, Exe);
                File.WriteAllBytes(fixture, []);
                UserDataPaths.RootOverride = root;
                DisplayQualityService.BackupDirOverride = Path.Combine(root, "display-quality");
                DisplayQualityService.ApiOverride = () => api;
                AppState.GamePath = fixture;
                var profile = api.AddProfile("existing", Exe);
                // 真实驱动里每项都有个继承值；这里同样铺满，界面才不是一片「驱动无此项」。
                foreach (var definition in NvdrsSettingCatalog.All)
                    profile.InheritedSettings[definition.SettingId] = definition.DriverDefault ?? 0;
                // 驱动不接受这一项：保存后读不回来 = 驱动不支持。
                api.DropOnSave.Add(DisplayQualityService.ShaderDiskCacheId);

                var compiled = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                compiled.InitializeComponent(); app = compiled;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                LangService.Apply(LangService.ZhCn);
                // 所有确认框自动接受（窗口仍然开在屏幕外）。
                DialogService.PrepareWindowForTest = dialog =>
                {
                    DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => dialog.DialogResult = true);
                };

                var page = new DisplayQualityView();
                owner = new Window { Content = page, Width = 1100, Height = 980 };
                app.MainWindow = owner;
                DlssFeedbackTests.PrepareBackgroundWindow(owner);
                owner.Show();
                DlssFeedbackTests.PumpUntil(() =>
                    ((FrameworkElement)page.FindName("DrsSupportedPanel")).Visibility == Visibility.Visible);
                Descendants(page).OfType<RadioButton>().Single(b => b.Tag as string == "drs").IsChecked = true;
                owner.UpdateLayout();
                Assert.True(((FrameworkElement)page.FindName("CardDrs")).IsVisible);

                // ---- 竞技预设：8 项成功、1 项驱动不支持 ----
                Click(page, "DrsPresetButton");
                DlssFeedbackTests.PumpUntil(() =>
                    ((FrameworkElement)page.FindName("DrsReceiptPanel")).Visibility == Visibility.Visible);
                var summary = (TextBlock)page.FindName("DrsReceiptSummary");
                var unsupported = (TextBlock)page.FindName("DrsReceiptUnsupported");
                var mismatch = (TextBlock)page.FindName("DrsReceiptMismatch");
                var retry = (Button)page.FindName("DrsRetryFailedButton");
                Assert.Equal(Str.T("Str.DrsReceiptApplied", 8), summary.Text);
                Assert.Equal(Visibility.Visible, unsupported.Visibility);
                Assert.Contains(Str.T("Str.RecipeCache"), unsupported.Text);   // 名字直接列出来
                Assert.Equal(Visibility.Collapsed, mismatch.Visibility);
                Assert.True(retry.IsEnabled);
                Assert.False(profile.Settings.ContainsKey(DisplayQualityService.ShaderDiskCacheId));
                Assert.Equal(16u, profile.Settings[DisplayQualityService.AnisoLevelId]);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(theme, animate: false);   // 渲染证据要稳定结果，不走 180ms 过渡
                    owner.UpdateLayout();
                    AssertReadable((FrameworkElement)page.FindName("DrsReceiptPanel"), "drs-receipt");
                    Render(page, "drs-receipt-" + theme);
                }

                // ---- 只重试失败项：其余项不动 ----
                api.DropOnSave.Clear();
                var textureBefore = profile.Settings[DisplayQualityService.TextureQualityId];
                Click(page, "DrsRetryFailedButton");
                // 回执要等重试真的写回并读回之后才刷新，所以等文案变，不等按钮变。
                DlssFeedbackTests.PumpUntil(() => summary.Text == Str.T("Str.DrsReceiptApplied", 1));
                DlssFeedbackTests.PumpUntil(() =>
                    unsupported.Visibility == Visibility.Collapsed && !retry.IsEnabled);
                Assert.Equal(1u, profile.Settings[DisplayQualityService.ShaderDiskCacheId]);
                Assert.Equal(textureBefore, profile.Settings[DisplayQualityService.TextureQualityId]);
                ThemeManager.Apply("dark", animate: false);
                owner.UpdateLayout();
                Render(page, "drs-receipt-retried-dark");

                // ---- 概览页：改过名称时同时显示「真实型号 / 当前上报」 ----
                RegistryHelper.SnapshotOverride = (_, _, name) => name switch
                {
                    "ClassGUID" => new(true, "{4d36e968-e325-11ce-bfc1-08002be10318}", RegistryValueKind.String),
                    "Driver" => new(true, @"{4d36e968-e325-11ce-bfc1-08002be10318}\0000", RegistryValueKind.String),
                    "HardwareID" => new(true, new[] { @"PCI\VEN_10DE&DEV_2F18" }, RegistryValueKind.MultiString),
                    "DriverVersion" => new(true, "1.0", RegistryValueKind.String),
                    "DriverDesc" => new(true, "NVIDIA GeForce RTX 3070", RegistryValueKind.String),
                    "DeviceDesc" => new(true, "@oem.inf;NVIDIA GeForce GTX 750 Ti", RegistryValueKind.String),
                    _ => new(false, null, RegistryValueKind.Unknown),
                };
                GpuIdentityService.DeviceIdsOverride = () => new[] { IdentityInstance };
                StateStore.SaveDetect(JsonNode.Parse(JsonSerializer.Serialize(new
                {
                    gamePath = fixture,
                    hardware = new { cpu = "Fixture CPU", gpu = "NVIDIA GeForce RTX 3070", ramGB = 16, os = "Windows 11", isLaptop = true, isAdmin = false },
                    checks = Array.Empty<object>(),
                    items = Array.Empty<object>(),
                }))!.AsObject());
                var detect = new DetectView();
                var detectHost = new Window { Content = detect, Width = 1100, Height = 900 };
                app.MainWindow = detectHost;
                DlssFeedbackTests.PrepareBackgroundWindow(detectHost);
                detectHost.Show();
                DlssFeedbackTests.PumpUntil(() => detect.IsLoaded);
                var gpuText = (TextBlock)detect.FindName("GpuText");
                var reportedText = (TextBlock)detect.FindName("GpuReportedText");
                // 概览页自己会异步推导一次伪装状态，等它落到「两行」。
                DlssFeedbackTests.PumpUntil(() => reportedText.Visibility == Visibility.Visible);
                Assert.Equal(Str.T("Str.DetectGpuReal", "NVIDIA GeForce RTX 3070"), gpuText.Text);
                Assert.Equal(Str.T("Str.DetectGpuReported", "NVIDIA GeForce GTX 750 Ti"), reportedText.Text);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(theme, animate: false);
                    detectHost.UpdateLayout();
                    AssertReadable((FrameworkElement)gpuText.Parent, "detect-gpu");
                    Render(detect, "detect-gpu-disguised-" + theme);
                }
                // 未伪装时保持单行：不给普通用户多加信息。
                detect.ApplyGpuIdentityLine("NVIDIA GeForce RTX 3070",
                    new GpuIdentitySnapshot("NVIDIA GeForce RTX 3070", "NVIDIA GeForce RTX 3070", false, null));
                Assert.Equal("NVIDIA GeForce RTX 3070", gpuText.Text);
                Assert.Equal(Visibility.Collapsed, reportedText.Visibility);
                detectHost.Close();

                // ---- 高级参数：三态 + 改成继承 ----
                var driver = new GameDriverSettings();
                // 展开态要看得见若干行：窗口给足高度，否则内容会被窗口裁掉。
                driverHost = new Window { Content = driver, Width = 980, Height = 1600 };
                app.MainWindow = driverHost;
                DlssFeedbackTests.PrepareBackgroundWindow(driverHost);
                driverHost.Show();
                DlssFeedbackTests.PumpUntil(() => driver.IsLoaded && ((Button)driver.FindName("ApplyDriverButton")).IsEnabled);
                var expander = (Expander)driver.FindName("AdvancedExpander");
                expander.IsExpanded = true;
                driverHost.UpdateLayout();
                var list = (ItemsControl)driver.FindName("AdvancedList");
                // 展开内容要真的量出来：可见性变化触发的刷新续体会在这几帧里跑完，
                // 早一步截图就会拍到「只有标题、没有行」。
                for (var i = 0; i < 3; i++) PumpIdle();
                DlssFeedbackTests.PumpUntil(() => driver.AdvancedRows.Count == NvdrsSettingCatalog.All.Count
                    && list.Items.Count == NvdrsSettingCatalog.All.Count && expander.ActualHeight > 300);
                driverHost.UpdateLayout();
                var rows = driver.AdvancedRows;
                Assert.Equal(Str.T("Str.AdvancedOverridden"),
                    rows.Single(r => r.SettingId == DisplayQualityService.TextureQualityId).StateLabel);
                Assert.Equal(Str.T("Str.AdvancedInherited"),
                    rows.Single(r => r.SettingId == DisplayQualityService.FrameRateLimitId).StateLabel);
                var aniso = rows.Single(r => r.SettingId == DisplayQualityService.AnisoLevelId);
                Assert.True(aniso.IsChoice);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(theme, animate: false);   // 渲染证据要稳定结果，不走 180ms 过渡
                    for (var i = 0; i < 2; i++) PumpIdle();
                    DlssFeedbackTests.PumpUntil(() => expander.ActualHeight > 300);
                    AssertReadable(expander, "advanced-params");
                    Render(expander, "advanced-params-" + theme);
                }
                aniso.SelectedChoice = aniso.Choices.Single(c => c.Value is null);   // 恢复继承
                Click(driver, "AdvancedWriteButton");
                DlssFeedbackTests.PumpUntil(() => !profile.Settings.ContainsKey(DisplayQualityService.AnisoLevelId));
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)driver.FindName("AdvancedStatus")).Text.Contains("1"));
                Assert.Equal(NvdrsSettingState.Inherited, DisplayQualityService.ReadAdvancedSettings(Exe)
                    .Items.Single(i => i.SettingId == DisplayQualityService.AnisoLevelId).State);

                // ---- F1：与当前型号相同的预设标出来并禁用「应用」 ----
                RegistryHelper.SnapshotOverride = (_, _, name) => name switch
                {
                    "ClassGUID" => new(true, "{4d36e968-e325-11ce-bfc1-08002be10318}", RegistryValueKind.String),
                    "Driver" => new(true, @"{4d36e968-e325-11ce-bfc1-08002be10318}\0000", RegistryValueKind.String),
                    "HardwareID" => new(true, new[] { @"PCI\VEN_10DE&DEV_2F18" }, RegistryValueKind.MultiString),
                    "DriverVersion" => new(true, "1.0", RegistryValueKind.String),
                    "DriverDesc" => new(true, "NVIDIA GeForce GTX 1050 Ti", RegistryValueKind.String),
                    "DeviceDesc" => new(true, "@oem.inf;NVIDIA GeForce GTX 1050 Ti", RegistryValueKind.String),
                    _ => new(false, null, RegistryValueKind.Unknown),
                };
                GpuIdentityService.DeviceIdsOverride = () => new[] { IdentityInstance };
                Descendants(page).OfType<RadioButton>().Single(b => b.Tag as string == "identity").IsChecked = true;
                owner.UpdateLayout();
                Click(page, "GpuIdentityRefreshButton");
                var presets = (ListBox)page.FindName("GpuIdentityPresets");
                var applyGpu = (Button)page.FindName("GpuIdentityApplyButton");
                DlssFeedbackTests.PumpUntil(() => presets.Items.Cast<GpuPresetItem>().Any(i => i.IsCurrent));
                var current = presets.Items.Cast<GpuPresetItem>().Single(i => i.IsCurrent);
                Assert.Equal(GpuIdentityService.TargetDescription, current.Description);
                presets.SelectedItem = current;
                owner.UpdateLayout();
                Assert.False(applyGpu.IsEnabled);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(theme, animate: false);   // 渲染证据要稳定结果，不走 180ms 过渡
                    owner.UpdateLayout();
                    AssertReadable((FrameworkElement)page.FindName("CardGpuIdentity"), "gpu-presets-current");
                    Render(page, "gpu-presets-current-" + theme);
                }
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                owner?.Close(); driverHost?.Close(); app?.Shutdown();
                GpuIdentityService.DeviceIdsOverride = null;
                RegistryHelper.SnapshotOverride = null;
                DialogService.PrepareWindowForTest = null;
                DisplayQualityService.ApiOverride = null;
                DisplayQualityService.BackupDirOverride = null;
                UserDataPaths.RootOverride = null;
                AppState.GamePath = null;
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Display receipt UI check did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Click(FrameworkElement root, string name)
        => ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>把排队工作（布局、异步续体）推进到空闲，再继续断言。</summary>
    private static void PumpIdle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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

    /// <summary>可读性守卫：本轮新增的面板里不许出现小于 13 号的可见文字。</summary>
    private static void AssertReadable(FrameworkElement root, string name)
    {
        root.UpdateLayout();
        var tooSmall = Descendants(root).OfType<TextBlock>()
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.FontSize < 13)
            .Select(t => $"{t.Text}({t.FontSize})").Distinct().ToArray();
        Assert.True(tooSmall.Length == 0, $"{name} 有小于 13 号的可见文字：" + string.Join(" | ", tooSmall.Take(6)));
    }

    private static void Render(FrameworkElement element, string name)
    {
        if (name.EndsWith("-light") || name.EndsWith("-dark"))
        {
            var expected = (Color)ColorConverter.ConvertFromString(name.EndsWith("-light") ? "#F7F9FC" : "#0C1015")!;
            DlssFeedbackTests.PumpUntil(() =>
                ((SolidColorBrush)Application.Current.FindResource("AppBackdropBrush")).Color == expected);
        }
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
        using var stream = File.Create(Path.Combine(Output(), name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
