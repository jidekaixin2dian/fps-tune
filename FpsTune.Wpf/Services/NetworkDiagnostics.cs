using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record NetworkAdapterInfo(string Name, string Status, string Addresses, string Dns, string Gateway, string Speed);
internal sealed record NetworkProbe(int Number, DateTime Time, double? Milliseconds, string Status);
internal sealed record NetworkProbeSummary(int Sent, int Received, double? Average, double? Minimum, double? Maximum, double? Variation)
{
    public double? FailurePercent => Sent == 0 ? null : 100d * (Sent - Received) / Sent;
    internal static NetworkProbeSummary From(IReadOnlyList<NetworkProbe> probes)
    {
        var values = probes.Where(p => p.Milliseconds.HasValue).Select(p => p.Milliseconds!.Value).ToArray();
        var variation = probes.Zip(probes.Skip(1)).Where(p => p.First.Milliseconds.HasValue && p.Second.Milliseconds.HasValue)
            .Select(p => Math.Abs(p.First.Milliseconds!.Value - p.Second.Milliseconds!.Value)).ToArray();
        return new(probes.Count, values.Length, values.Length == 0 ? null : values.Average(), values.Length == 0 ? null : values.Min(),
            values.Length == 0 ? null : values.Max(), variation.Length == 0 ? null : variation.Average());
    }
}
internal sealed record NetworkProbeResult(string Host, string Mode, int Port, string Resolved, string Address, double? DnsMilliseconds,
    IReadOnlyList<NetworkProbe> Probes, bool Cancelled, string? Error)
{
    public NetworkProbeSummary Summary => NetworkProbeSummary.From(Probes);
    public string ToText()
    {
        var b = new StringBuilder().AppendLine("FPS 帧律 · 网络诊断").AppendLine($"目标：{Host} · {Mode}" + (Mode == "TCP" ? $":{Port}" : ""))
            .AppendLine($"解析：{Resolved}；测试地址：{Address}；DNS：{Format(DnsMilliseconds)}")
            .AppendLine($"状态：{(Cancelled ? "已停止" : Error is not null ? Error : "已完成")}");
        var s = Summary;
        b.AppendLine($"已测 {s.Sent} / 响应 {s.Received}；未成功比例：{(s.FailurePercent is { } f ? f.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—")}")
            .AppendLine($"平均：{Format(s.Average)}；最低：{Format(s.Minimum)}；最高：{Format(s.Maximum)}；相邻响应波动：{Format(s.Variation)}");
        foreach (var p in Probes) b.AppendLine($"{p.Number}\t{p.Time:HH:mm:ss}\t{Format(p.Milliseconds)}\t{p.Status}");
        b.AppendLine(Mode == "ICMP" ? "ICMP 未响应可能由目标禁用回包或网络路径引起。" : "TCP 结果表示指定端口的建连情况。");
        return b.ToString();
    }
    internal static string Format(double? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "—";
}
internal interface INetworkProbeTransport
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken token);
    Task<(double? Milliseconds, string Status)> ProbeAsync(IPAddress address, int? port, CancellationToken token);
}
internal sealed class SystemNetworkProbeTransport : INetworkProbeTransport
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) => Dns.GetHostAddressesAsync(host, token);
    public async Task<(double? Milliseconds, string Status)> ProbeAsync(IPAddress address, int? port, CancellationToken token)
    {
        if (port is { } tcpPort)
        {
            using var client = new TcpClient(address.AddressFamily);
            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(address, tcpPort, token);
            return (watch.Elapsed.TotalMilliseconds, "连接成功");
        }
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(2), new byte[32], new PingOptions(), token);
        return reply.Status == IPStatus.Success ? (reply.RoundtripTime, "响应成功") : (null, reply.Status.ToString());
    }
}
internal static class NetworkDiagnostics
{
    internal static string NormalizeHost(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        if (value.Length is 0 or > 253 || value.Any(char.IsControl)) throw new ArgumentException("请输入域名或 IP 地址，不含网址路径或端口。");
        if (IPAddress.TryParse(value, out var address))
        {
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast
                || address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239)
                throw new ArgumentException("请选择单个主机地址。");
            return address.ToString();
        }
        try { value = new IdnMapping().GetAscii(value); } catch (ArgumentException) { throw new ArgumentException("域名格式无效。"); }
        if (Uri.CheckHostName(value) != UriHostNameType.Dns || value.Contains(':') || value.Contains('/') || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("请输入域名或 IP 地址，不含网址路径或端口。");
        return value;
    }
    internal static async Task<NetworkProbeResult> RunAsync(string host, int? port, int count, IProgress<NetworkProbe>? progress,
        CancellationToken token, INetworkProbeTransport? transport = null, TimeSpan? interval = null)
    {
        host = NormalizeHost(host);
        if (port is < 1 or > 65535 || count is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(count));
        transport ??= new SystemNetworkProbeTransport();
        var probes = new List<NetworkProbe>(); string resolved = "—", selected = "—"; double? dnsMs = null;
        var mode = port.HasValue ? "TCP" : "ICMP";
        try
        {
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
            else
            {
                using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); dnsTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                var timer = Stopwatch.StartNew(); addresses = await transport.ResolveAsync(host, dnsTimeout.Token); dnsMs = timer.Elapsed.TotalMilliseconds;
            }
            token.ThrowIfCancellationRequested();
            addresses = addresses.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).Distinct().ToArray();
            if (addresses.Length == 0) throw new IOException("DNS 未返回可用地址。");
            resolved = string.Join(", ", addresses.Select(a => a.ToString()));
            var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0]; selected = target.ToString();
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                (double? Milliseconds, string Status) result;
                try { result = await transport.ProbeAsync(target, port, timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { result = (null, "超时"); }
                catch (Exception ex) when (ex is SocketException or PingException or UnauthorizedAccessException)
                { result = (null, PrivacyScrub.Sanitize(ex.Message)); }
                token.ThrowIfCancellationRequested();
                if (result.Milliseconds is { } ms && (!double.IsFinite(ms) || ms < 0)) result = (null, "未取得有效耗时");
                var probe = new NetworkProbe(i + 1, DateTime.Now, result.Milliseconds, result.Status);
                probes.Add(probe); progress?.Report(probe);
                if (i + 1 < count) await Task.Delay(interval ?? TimeSpan.FromMilliseconds(250), token);
            }
            return new(host, mode, port ?? 0, resolved, selected, dnsMs, probes, false, null);
        }
        catch (OperationCanceledException)
        { return new(host, mode, port ?? 0, resolved, selected, dnsMs, probes, token.IsCancellationRequested, token.IsCancellationRequested ? null : "DNS 解析超时。"); }
        catch (Exception ex) when (ex is SocketException or IOException or NetworkInformationException)
        { return new(host, mode, port ?? 0, resolved, selected, dnsMs, probes, false, PrivacyScrub.Sanitize(ex.Message)); }
    }
    internal static IReadOnlyList<NetworkAdapterInfo> ReadAdapters()
    {
        var list = new List<NetworkAdapterInfo>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var ip = n.GetIPProperties();
                list.Add(new(n.Name, n.OperationalStatus == OperationalStatus.Up ? "已连接" : n.OperationalStatus.ToString(),
                    string.Join(" · ", ip.UnicastAddresses.Select(a => a.Address)), string.Join(" · ", ip.DnsAddresses),
                    ip.GatewayAddresses.FirstOrDefault(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any))?.Address.ToString() ?? "",
                    n.Speed > 0 ? $"{n.Speed / 1_000_000d:0.#} Mbps" : "—"));
            }
            catch (NetworkInformationException ex) { list.Add(new(n.Name, "信息不可用：" + ex.Message, "—", "—", "", "—")); }
        }
        return list.OrderBy(n => n.Status == "已连接" ? 0 : 1).ThenBy(n => n.Name).ToArray();
    }
    internal static void Export(string path, string text) => AtomicFile.WriteAllTextDurable(path, text, new UTF8Encoding(false));
}
