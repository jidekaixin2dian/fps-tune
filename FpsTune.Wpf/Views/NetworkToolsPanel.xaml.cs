using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;

namespace FpsTune.Wpf.Views;

public partial class NetworkToolsPanel : UserControl
{
    private readonly INetworkProbeTransport? _transport;
    private readonly Func<IReadOnlyList<NetworkAdapterInfo>> _readAdapters;
    private readonly Func<EndpointSnapshot> _readEndpoints;
    private CancellationTokenSource? _run;
    private NetworkProbeResult? _result;
    private EndpointSnapshot? _snapshot;
    private IReadOnlyList<NetworkEndpoint> _visible = [];
    private int _adapterGeneration, _endpointGeneration;
    private bool _portsBusy;
    private sealed record ProbeRow(int Number, string Duration, string Status);
    private readonly ObservableCollection<ProbeRow> _rows = [];
    public NetworkToolsPanel() : this(null, NetworkDiagnostics.ReadAdapters, NetworkEndpoints.Read) { }
    internal NetworkToolsPanel(INetworkProbeTransport? transport, Func<IReadOnlyList<NetworkAdapterInfo>> adapters, Func<EndpointSnapshot> endpoints)
    {
        _transport = transport; _readAdapters = adapters; _readEndpoints = endpoints;
        InitializeComponent(); ProbeRows.ItemsSource = _rows;
        Loaded += async (_, _) => { await RefreshAdaptersAsync(); if (PortsTab.IsChecked == true) await RefreshPortsAsync(); };
        Unloaded += (_, _) => { _run?.Cancel(); _adapterGeneration++; _endpointGeneration++; };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _run?.Cancel(); };
    }
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ProbePane is null) return;
        var ports = PortsTab.IsChecked == true;
        ProbePane.Visibility = ports ? Visibility.Collapsed : Visibility.Visible; PortsPane.Visibility = ports ? Visibility.Visible : Visibility.Collapsed;
        UiMotion.Enter(ports ? PortsPane : ProbePane);
        if (ports) { _run?.Cancel(); if (IsLoaded && _snapshot is null) _ = RefreshPortsAsync(); }
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    { if (PortBox is not null) PortBox.IsEnabled = ModeBox.SelectedIndex == 1 && _run is null; }
    private void Adapter_Changed(object sender, SelectionChangedEventArgs e)
        => ShowAdapter();
    private void ShowAdapter()
    {
        if (AdapterText is null) return;
        var adapter = AdapterBox.SelectedItem as NetworkAdapterInfo;
        AdapterText.Text = adapter is null ? "未读取到可用网卡。" : $"{adapter.Status} · 链路 {adapter.Speed}\n地址：{adapter.Addresses}\nDNS：{adapter.Dns}\n网关：{(adapter.Gateway.Length == 0 ? "—" : adapter.Gateway)}";
        GatewayButton.IsEnabled = adapter is { Gateway.Length: > 0 } && _run is null;
    }
    private async void AdaptersRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAdaptersAsync();
    private async Task RefreshAdaptersAsync()
    {
        var generation = ++_adapterGeneration; var selected = (AdapterBox.SelectedItem as NetworkAdapterInfo)?.Name;
        AdaptersRefreshButton.IsEnabled = false;
        try
        {
            var rows = await Task.Run(_readAdapters);
            if (generation != _adapterGeneration || !IsLoaded) return;
            AdapterBox.ItemsSource = rows;
            AdapterBox.SelectedItem = rows.FirstOrDefault(a => a.Name == selected) ?? rows.FirstOrDefault(a => a.Status == "已连接" && a.Gateway.Length > 0) ?? rows.FirstOrDefault();
            ShowAdapter();
        }
        catch (Exception ex) { if (generation == _adapterGeneration) { AdapterText.Text = PrivacyScrub.Sanitize(ex.Message); GatewayButton.IsEnabled = false; } }
        finally { AdaptersRefreshButton.IsEnabled = _run is null; }
    }
    internal void ShowPorts(bool ports) { if (ports) PortsTab.IsChecked = true; else ProbeTab.IsChecked = true; }
    private void Gateway_Click(object sender, RoutedEventArgs e)
    { if (_run is null && AdapterBox.SelectedItem is NetworkAdapterInfo { Gateway.Length: > 0 } adapter) HostBox.Text = adapter.Gateway; }
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunProbeAsync();
    internal async Task RunProbeAsync()
    {
        if (_run is not null) return;
        string host; int? port = null;
        try
        {
            host = NetworkDiagnostics.NormalizeHost(HostBox.Text);
            if (ModeBox.SelectedIndex == 1)
            {
                if (!int.TryParse(PortBox.Text, out var n) || n is < 1 or > 65535) throw new ArgumentException("TCP 端口应为 1–65535。");
                port = n;
            }
        }
        catch (ArgumentException ex) { ProbeStatus.Text = ex.Message; return; }
        var count = CountBox.SelectedIndex switch { 0 => 4, 2 => 16, _ => 8 };
        using var cancellation = new CancellationTokenSource(); _run = cancellation;
        _result = null; _rows.Clear(); ExportProbeButton.IsEnabled = false;
        ProbeProgress.Maximum = count; ProbeProgress.Value = 0; ResultTarget.Text = host + (port is { } p ? $" · TCP {p}" : " · ICMP");
        FailureLabel.Text = port.HasValue ? "连接失败比例" : "未响应比例";
        AverageText.Text = FailureText.Text = VariationText.Text = "—"; RangeText.Text = "";
        SetProbeBusy(true); ProbeStatus.Text = "正在解析目标并采样…";
        var progress = new Progress<NetworkProbe>(probe =>
        {
            if (!ReferenceEquals(_run, cancellation) || cancellation.IsCancellationRequested) return;
            _rows.Add(new(probe.Number, NetworkProbeResult.Format(probe.Milliseconds), probe.Status)); ProbeProgress.Value = probe.Number;
            ProbeStatus.Text = $"正在采样 {probe.Number} / {count}";
        });
        try
        {
            _result = await NetworkDiagnostics.RunAsync(host, port, count, progress, cancellation.Token, _transport);
            _rows.Clear(); foreach (var probe in _result.Probes) _rows.Add(new(probe.Number, NetworkProbeResult.Format(probe.Milliseconds), probe.Status));
            var s = _result.Summary;
            ProbeProgress.Value = s.Sent;
            ResultTarget.Text = $"{_result.Host} · {_result.Mode} · {_result.Address}\nDNS：{NetworkProbeResult.Format(_result.DnsMilliseconds)} · 解析地址：{_result.Resolved}";
            AverageText.Text = NetworkProbeResult.Format(s.Average); FailureText.Text = s.FailurePercent is { } f ? $"{f:0.#}%" : "—";
            VariationText.Text = NetworkProbeResult.Format(s.Variation); RangeText.Text = $"最低 {NetworkProbeResult.Format(s.Minimum)} · 最高 {NetworkProbeResult.Format(s.Maximum)} · 响应 {s.Received}/{s.Sent}";
            ProbeStatus.Text = _result.Cancelled ? $"已停止，保留 {s.Sent} 次采样。" : _result.Error ?? (port.HasValue ? "测试完成：查看指定端口的建连情况。" : "测试完成。未响应可能来自目标禁用 ICMP 回包或网络路径。");
            ExportProbeButton.IsEnabled = true;
        }
        catch (Exception ex) { ProbeStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _run = null; SetProbeBusy(false); }
    }
    private void SetProbeBusy(bool busy)
    {
        HostBox.IsEnabled = ModeBox.IsEnabled = CountBox.IsEnabled = StartButton.IsEnabled = AdapterBox.IsEnabled = AdaptersRefreshButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy; PortBox.IsEnabled = !busy && ModeBox.SelectedIndex == 1;
        GatewayButton.IsEnabled = !busy && AdapterBox.SelectedItem is NetworkAdapterInfo { Gateway.Length: > 0 };
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { _run?.Cancel(); StopButton.IsEnabled = false; }
    private async void PortsRefresh_Click(object sender, RoutedEventArgs e) => await RefreshPortsAsync();
    internal async Task RefreshPortsAsync()
    {
        if (_portsBusy) return;
        _portsBusy = true; var generation = ++_endpointGeneration;
        PortsRefreshButton.IsEnabled = ExportPortsButton.IsEnabled = false; PortsStatus.Text = "正在读取端口快照…";
        try
        {
            var snapshot = await Task.Run(_readEndpoints);
            if (generation != _endpointGeneration || !IsLoaded) return;
            _snapshot = snapshot; ApplyFilter();
        }
        catch (Exception ex)
        {
            if (generation != _endpointGeneration) return;
            _snapshot = null; _visible = []; EndpointList.ItemsSource = null; PortsStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
        finally { _portsBusy = false; PortsRefreshButton.IsEnabled = true; }
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (EndpointList is not null && !_portsBusy) ApplyFilter(); }
    private void ListenerFilter_Changed(object sender, RoutedEventArgs e) { if (EndpointList is not null && !_portsBusy) ApplyFilter(); }
    private void ApplyFilter()
    {
        var text = PortFilterBox.Text.Trim(); var q = EndpointSearchBox.Text.Trim(); int? port = null;
        if (text.Length > 0)
        {
            if (!int.TryParse(text, out var p) || p is < 0 or > 65535)
            { _visible = []; EndpointList.ItemsSource = null; ExportPortsButton.IsEnabled = false; PortsStatus.Text = "筛选端口应为 0–65535，留空显示全部。"; return; }
            port = p;
        }
        _visible = (_snapshot?.Items ?? []).Where(e => (!port.HasValue || e.LocalPort == port) && (ListenersOnly.IsChecked != true || e.Listening)
            && (e.Process.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Local.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Remote.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Pid.ToString().Contains(q))).ToArray();
        EndpointList.ItemsSource = _visible; ExportPortsButton.IsEnabled = _snapshot is not null;
        PortsStatus.Text = _snapshot is null ? "点击刷新读取当前端口。" : $"显示 {_visible.Count} / {_snapshot.Items.Count} 条 · {_snapshot.CapturedAt:HH:mm:ss}" + (_visible.Count == 0 ? " · 没有匹配端口" : "")
            + (_snapshot.Warnings.Count > 0 ? "\n" + string.Join("\n", _snapshot.Warnings) : "");
    }
    internal void ExportProbeTo(string path) => NetworkDiagnostics.Export(path, _result?.ToText() ?? throw new InvalidOperationException("请先完成一次测试。"));
    internal void ExportPortsTo(string path) => NetworkDiagnostics.Export(path, _snapshot?.ToText(_visible) ?? throw new InvalidOperationException("请先读取端口快照。"));
    private static string? PickReportPath(string name)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出诊断结果", Filter = "文本文件|*.txt", DefaultExt = ".txt", FileName = $"FpsTune-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private void ExportProbe_Click(object sender, RoutedEventArgs e)
    {
        var result = _result; if (result is null || _run is not null) return;
        var path = PickReportPath("Network"); if (path is null) return;
        try { NetworkDiagnostics.Export(path, result.ToText()); ProbeStatus.Text = "结果已导出。"; } catch (Exception ex) { ProbeStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void ExportPorts_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _snapshot; var rows = _visible.ToArray(); if (snapshot is null) return;
        var path = PickReportPath("Ports"); if (path is null) return;
        try { NetworkDiagnostics.Export(path, snapshot.ToText(rows)); } catch (Exception ex) { PortsStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
}
