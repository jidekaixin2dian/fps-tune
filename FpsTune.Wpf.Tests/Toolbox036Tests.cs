using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using FpsTune.Wpf.Views.Controls;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class Toolbox036Tests
{
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "toolbox036-20261030");
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    [Fact]
    public void Network_targets_units_missing_samples_and_dns_errors_keep_results_truthful()
    {
        Assert.Equal("::1", NetworkDiagnostics.NormalizeHost("[::1]"));
        Assert.Equal("example.com", NetworkDiagnostics.NormalizeHost(" example.com "));
        foreach (var target in new[] { "https://example.com/a", "example.com:443", "bad\nname", "0.0.0.0", "ff02::1", "224.0.0.1", "" })
            Assert.Throws<ArgumentException>(() => NetworkDiagnostics.NormalizeHost(target));
        var now = DateTime.Now;
        var s = NetworkProbeSummary.From([new(1, now, 0, "ok"), new(2, now, 10, "ok"), new(3, now, null, "timeout"), new(4, now, 20, "ok")]);
        Assert.Equal(10, s.Average); Assert.Equal(25, s.FailurePercent); Assert.Equal(10, s.Variation); Assert.Equal(0, s.Minimum);
        var none = NetworkProbeSummary.From([]); Assert.Null(none.Average); Assert.Null(none.FailurePercent); Assert.Null(none.Variation);
        Assert.Null(NetworkProbeSummary.From([new(1, now, 1, "ok"), new(2, now, null, "timeout"), new(3, now, 5, "ok")]).Variation);
        Assert.Throws<InvalidDataException>(() => NetworkEndpoints.Parse([1, 0, 0, 0], false, false));
        Assert.Throws<InvalidDataException>(() => NetworkEndpoints.Parse([255, 255, 255, 255], true, true));
    }
    [Fact]
    public async Task Network_diagnostics_pin_the_resolved_address_and_cancel_without_inventing_loss()
    {
        var fake = new Transport { Values = [5, null, 0, 9] };
        var result = await NetworkDiagnostics.RunAsync("fixture.example", 443, 4, null, CancellationToken.None, fake, TimeSpan.Zero);
        Assert.Equal(1, fake.Resolutions); Assert.Equal(4, result.Summary.Sent); Assert.Equal(3, result.Summary.Received);
        Assert.All(fake.Addresses, address => Assert.Equal(IPAddress.Loopback, address));
        Assert.Contains("::1", result.Resolved); Assert.Equal("127.0.0.1", result.Address); Assert.Contains("TCP:443", result.ToText());
        var failed = await NetworkDiagnostics.RunAsync("missing.example", null, 4, null, CancellationToken.None, new Transport { FailDns = true });
        Assert.NotNull(failed.Error); Assert.Equal(0, failed.Summary.Sent); Assert.Null(failed.Summary.FailurePercent);
        using var cancel = new CancellationTokenSource(); var blocked = new Transport { Block = true };
        var pending = NetworkDiagnostics.RunAsync("127.0.0.1", null, 4, null, cancel.Token, blocked);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        var stopped = await pending; Assert.True(stopped.Cancelled); Assert.Equal(0, stopped.Summary.Sent); Assert.Null(stopped.Summary.FailurePercent);
    }
    [Fact]
    public async Task Native_owner_tables_match_live_loopback_tcp_udp_and_connectivity_without_external_traffic()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var tcp6 = Socket.OSSupportsIPv6 ? new TcpListener(IPAddress.IPv6Loopback, 0) : null;
        using var udp6 = Socket.OSSupportsIPv6 ? new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0)) : null;
        tcp6?.Start();
        var table = NetworkEndpoints.Read(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        Assert.Contains(table.Items, e => e.Protocol == "TCPv4" && e.LocalPort == port && e.Pid == Environment.ProcessId && e.Listening);
        Assert.Contains(table.Items, e => e.Protocol == "UDPv4" && e.LocalPort == ((IPEndPoint)udp.Client.LocalEndPoint!).Port && e.Pid == Environment.ProcessId);
        if (tcp6 is not null)
        {
            Assert.Contains(table.Items, e => e.Protocol == "TCPv6" && e.LocalPort == ((IPEndPoint)tcp6.LocalEndpoint).Port && e.Pid == Environment.ProcessId);
            Assert.Contains(table.Items, e => e.Protocol == "UDPv6" && e.LocalPort == ((IPEndPoint)udp6!.Client.LocalEndPoint!).Port && e.Pid == Environment.ProcessId);
        }
        var result = await NetworkDiagnostics.RunAsync("127.0.0.1", port, 1, null, CancellationToken.None);
        Assert.Equal(1, result.Summary.Received); Assert.Null(result.DnsMilliseconds); Assert.Null(result.Error);
        var echo = await NetworkDiagnostics.RunAsync("127.0.0.1", null, 1, null, CancellationToken.None);
        Assert.Equal(1, echo.Summary.Received); Assert.Null(echo.Error);
        var localDns = await NetworkDiagnostics.RunAsync("localhost", port, 1, null, CancellationToken.None);
        Assert.Equal(1, localDns.Summary.Received); Assert.NotNull(localDns.DnsMilliseconds); Assert.Null(localDns.Error);
    }
    [Fact]
    public void Startup_navigation_missing_home_signals_deadline_and_early_exit_do_not_leave_a_splash()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(Toolbox036Tests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            UiMotion.EnabledOverride = false;
            try
            {
                foreach (var route in new[] { "games", "settings", "tools" })
                {
                    var main = new MainWindow(); app.MainWindow = main; DlssFeedbackTests.PrepareBackgroundWindow(main);
                    var splash = new SplashWindow(); DlssFeedbackTests.PrepareBackgroundWindow(splash); splash.Show();
                    var signals = 0; main.HomeDataReady += (_, _) => signals++;
                    var finish = StartupPresentation.CompleteAsync(main, splash, TimeSpan.FromSeconds(5));
                    main.NavigateTo(route); Assert.False(finish.IsCompleted); // Navigation alone cannot bypass the first frame.
                    main.Show();
                    DlssFeedbackTests.PumpUntil(() => finish.IsCompleted && !splash.IsVisible);
                    Assert.Equal(0, signals); Assert.True(main.IsVisible); main.Close();
                }
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme); var splash = new SplashWindow(); DlssFeedbackTests.PrepareBackgroundWindow(splash); splash.Show();
                    splash.SetPhase(92, "正在载入首页检测与实时采样"); Render(splash, "splash-" + theme);
                    Assert.True(((Button)splash.FindName("EnterButton")).IsEnabled);
                    var main = new MainWindow(); app.MainWindow = main;
                    var finish = StartupPresentation.CompleteAsync(main, splash);
                    Click(splash, "EnterButton"); DlssFeedbackTests.PumpUntil(() => finish.IsCompleted); Assert.False(splash.IsVisible);
                    splash.CloseWithFade(); main.Close();
                }
                var timeoutMain = new MainWindow(); var timedSplash = new SplashWindow(); DlssFeedbackTests.PrepareBackgroundWindow(timedSplash); timedSplash.Show();
                var timeout = StartupPresentation.CompleteAsync(timeoutMain, timedSplash, TimeSpan.FromMilliseconds(40));
                DlssFeedbackTests.PumpUntil(() => timeout.IsCompleted); Assert.False(timedSplash.IsVisible); timeoutMain.Close();
            }
            finally { UiMotion.EnabledOverride = null; }
        });
    }
    [Fact]
    public void Shared_feedback_reverses_and_clears_when_reduced_motion_or_unloading_applies()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(Toolbox036Tests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var button = new Button { Content = "操作", Style = (Style)app.FindResource("PrimaryButtonStyle") };
            var window = new Window { Content = button, Width = 300, Height = 160 }; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout();
            try
            {
                UiMotion.EnabledOverride = true; Assert.True(UiMotion.GetButtonFeedback(button));
                UiMotion.Press(button, true); var root = (FrameworkElement)button.Template.FindName("Root", button);
                var scale = Assert.IsType<ScaleTransform>(root.RenderTransform);
                DlssFeedbackTests.PumpUntil(() => scale.ScaleX < 0.999); UiMotion.Press(button, false);
                DlssFeedbackTests.PumpUntil(() => Math.Abs(scale.ScaleX - 1) < 0.00001);
                UiMotion.Press(button, true); UiMotion.EnabledOverride = false; UiMotion.Press(button, false); Assert.Equal(1, scale.ScaleX);
                UiMotion.Enter(button); Assert.Equal(1, button.Opacity);
                UiMotion.EnabledOverride = true; UiMotion.Press(button, true); button.IsEnabled = false; Assert.Equal(1, scale.ScaleX);
                button.IsEnabled = true; UiMotion.Press(button, true); window.Close(); DlssFeedbackTests.PumpUntil(() => scale.ScaleX == 1);
            }
            finally { window.Close(); UiMotion.EnabledOverride = null; }
        });
    }
    [Fact]
    public void Network_tools_actual_buttons_cancel_export_filter_and_render_in_themes_and_layouts()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(Toolbox036Tests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var fake = new Transport { Values = [12, null, 18, 15] };
            var snapshot = new EndpointSnapshot(DateTime.Now, [new("TCPv4", "127.0.0.1:443", 443, "—", "监听", 1234, "游戏工具"),
                new("TCPv6", "[::1]:8080", 8080, "[::1]:9000", "已连接", 4567, "本地服务"), new("UDPv6", "[::]:5353", 5353, "—", "UDP 绑定", 1234, "游戏工具")], []);
            var panel = new NetworkToolsPanel(fake, () => [new("测试网卡", "已连接", "192.0.2.10", "192.0.2.53", "192.0.2.1", "1000 Mbps")], () => snapshot);
            var main = new MainWindow { Width = 1180, Height = 720 }; app.MainWindow = main; DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show();
            try
            {
                main.NavigateTo("tools"); var maintenance = (MaintenanceView)((ContentControl)main.FindName("PageHost")).Content;
                ((ContentControl)maintenance.FindName("NetworkSection")).Content = panel; maintenance.OpenNetworkTool("network");
                DlssFeedbackTests.PumpUntil(() => ((ComboBox)panel.FindName("AdapterBox")).Items.Count == 1);
                Click(panel, "GatewayButton"); Assert.Equal("192.0.2.1", ((TextBox)panel.FindName("HostBox")).Text);
                ((TextBox)panel.FindName("HostBox")).Text = "fixture.example"; ((ComboBox)panel.FindName("CountBox")).SelectedIndex = 0;
                Click(panel, "StartButton"); Assert.False(((TextBox)panel.FindName("HostBox")).IsEnabled);
                DlssFeedbackTests.PumpUntil(() => ((Button)panel.FindName("ExportProbeButton")).IsEnabled);
                Assert.Equal("25%", ((TextBlock)panel.FindName("FailureText")).Text);
                panel.ExportProbeTo(Path.Combine(root, "probe.txt")); Assert.Contains("fixture.example", File.ReadAllText(Path.Combine(root, "probe.txt")));
                main.ExecuteWorkspaceCommand(WorkspaceCommands.Build().Single(c => c.Id == "tools:ports"));
                DlssFeedbackTests.PumpUntil(() => ((ListBox)panel.FindName("EndpointList")).Items.Count == 2);
                ((TextBox)panel.FindName("PortFilterBox")).Text = "443"; Assert.Single(((ListBox)panel.FindName("EndpointList")).Items.Cast<object>());
                panel.ExportPortsTo(Path.Combine(root, "ports.txt")); var text = File.ReadAllText(Path.Combine(root, "ports.txt")); Assert.Contains("127.0.0.1:443", text); Assert.DoesNotContain("5353", text);
                ((TextBox)panel.FindName("PortFilterBox")).Text = "bad"; Assert.False(((Button)panel.FindName("ExportPortsButton")).IsEnabled);
                ((TextBox)panel.FindName("PortFilterBox")).Text = ""; ((CheckBox)panel.FindName("ListenersOnly")).IsChecked = false;
                Assert.Equal(3, ((ListBox)panel.FindName("EndpointList")).Items.Count);
                foreach (var theme in new[] { "light", "dark" }) foreach (var mode in new[] { "studio", "console" })
                {
                    SettingsService.Current.ThemeMode = theme; ThemeManager.SetMode(theme); main.SetDisplayMode(mode);
                    var expected = (Color)ColorConverter.ConvertFromString(theme == "light" ? "#F7F9FC" : "#0C1015")!;
                    DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)app.FindResource("AppBackdropBrush")).Color == expected);
                    panel.ShowPorts(false); main.UpdateLayout(); Render(main, $"network-{mode}-{theme}");
                    ((ScrollViewer)panel.FindName("ProbePane")).ScrollToEnd(); Render(main, $"network-result-{mode}-{theme}");
                    ((ScrollViewer)panel.FindName("ProbePane")).ScrollToTop();
                    panel.ShowPorts(true); main.UpdateLayout(); Render(main, $"ports-{mode}-{theme}");
                }
                panel.ShowPorts(false); fake.Block = true; fake.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Click(panel, "StartButton"); DlssFeedbackTests.PumpUntil(() => fake.Entered.Task.IsCompleted); Click(panel, "StopButton");
                DlssFeedbackTests.PumpUntil(() => ((Button)panel.FindName("StartButton")).IsEnabled); Assert.Contains("已停止", ((TextBlock)panel.FindName("ProbeStatus")).Text);
                fake.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(panel, "StartButton");
                DlssFeedbackTests.PumpUntil(() => fake.Entered.Task.IsCompleted); ((RadioButton)maintenance.FindName("StorageNav")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => ((Button)panel.FindName("StartButton")).IsEnabled); Assert.Contains("已停止", ((TextBlock)panel.FindName("ProbeStatus")).Text);
            }
            finally { main.Close(); }
        });
    }
    private sealed class Transport : INetworkProbeTransport
    {
        public double?[] Values = [10]; public bool Block, FailDns; public int Resolutions; private int _next;
        public List<IPAddress> Addresses = []; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
        {
            Resolutions++; return FailDns ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)) : Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback });
        }
        public async Task<(double? Milliseconds, string Status)> ProbeAsync(IPAddress address, int? port, CancellationToken token)
        {
            Addresses.Add(address); Entered.TrySetResult(); if (Block) await Task.Delay(Timeout.Infinite, token);
            var value = Values[_next++ % Values.Length]; return (value, value.HasValue ? "响应成功" : "超时");
        }
    }
    private static void Click(FrameworkElement parent, string name) => ((Button)parent.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Render(Window window, string name)
    {
        var root = (FrameworkElement)window.Content; root.UpdateLayout();
        var frame = new System.Windows.Threading.DispatcherFrame();
        root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(background);
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill }, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(visual); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.True(pixels.Chunk(4).Select(p => BitConverter.ToInt32(p)).Distinct().Take(20).Count() > 10);
        Directory.CreateDirectory(Path.Combine(Output, "ui")); using var file = File.Create(Path.Combine(Output, "ui", name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
    }
}
