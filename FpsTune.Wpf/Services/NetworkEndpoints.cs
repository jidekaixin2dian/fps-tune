using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace FpsTune.Wpf.Services;

internal sealed record NetworkEndpoint(string Protocol, string Local, int LocalPort, string Remote, string State, int Pid, string Process)
{
    public bool Listening => State == "监听" || Protocol.StartsWith("UDP", StringComparison.Ordinal);
}
internal sealed record EndpointSnapshot(DateTime CapturedAt, IReadOnlyList<NetworkEndpoint> Items, IReadOnlyList<string> Warnings)
{
    internal string ToText(IEnumerable<NetworkEndpoint> visible)
    {
        var b = new StringBuilder().AppendLine($"FPS 帧律 · 端口快照 · {CapturedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine("协议\t本地地址\t远端地址\t状态\tPID\t进程");
        foreach (var e in visible) b.AppendLine($"{e.Protocol}\t{e.Local}\t{e.Remote}\t{e.State}\t{e.Pid}\t{e.Process.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')}");
        foreach (var warning in Warnings) b.AppendLine(warning);
        return b.ToString();
    }
}
/// <summary>Read-only OWNER_PID tables; no DNS lookup, process injection or connection termination.</summary>
internal static class NetworkEndpoints
{
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    internal static EndpointSnapshot Read()
    {
        var rows = new List<NetworkEndpoint>(); var warnings = new List<string>(); var names = new Dictionary<int, string>();
        foreach (var (v6, udp) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var label = (udp ? "UDP" : "TCP") + (v6 ? "v6" : "v4");
            try
            {
                foreach (var row in Parse(ReadTable(v6, udp), v6, udp))
                {
                    if (!names.TryGetValue(row.Pid, out var name))
                    {
                        try { using var p = Process.GetProcessById(row.Pid); name = p.ProcessName; }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { name = "进程已退出或不可访问"; }
                        names[row.Pid] = name;
                    }
                    rows.Add(row with { Process = name });
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
            { warnings.Add(label + " 未读取：" + PrivacyScrub.Sanitize(ex.Message)); }
        }
        return new(DateTime.Now, rows.OrderBy(e => e.LocalPort).ThenBy(e => e.Protocol).ThenBy(e => e.Pid).ToArray(), warnings);
    }
    private static byte[] ReadTable(bool v6, bool udp)
    {
        var size = 0;
        uint Read(IntPtr table) => udp ? GetExtendedUdpTable(table, ref size, false, v6 ? 23 : 2, 1, 0)
            : GetExtendedTcpTable(table, ref size, false, v6 ? 23 : 2, 5, 0);
        var status = Read(IntPtr.Zero);
        if (status is not (0 or 122)) throw new Win32Exception((int)status);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > 32 * 1024 * 1024) throw new InvalidDataException("端口表大小无效。");
            var capacity = size; var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                status = Read(buffer);
                if (status == 122) continue;
                if (status != 0) throw new Win32Exception((int)status);
                if (size < 4 || size > capacity) throw new InvalidDataException("端口表长度无效。");
                var bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, size); return bytes;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidDataException("端口列表变化较快，请刷新后重试。");
    }
    internal static IReadOnlyList<NetworkEndpoint> Parse(byte[] bytes, bool v6, bool udp)
    {
        var span = bytes.AsSpan(); var stride = udp ? v6 ? 28 : 12 : v6 ? 56 : 24;
        if (span.Length < 4) throw new InvalidDataException("端口表不完整。");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (count > 50000 || (long)count * stride + 4 > span.Length) throw new InvalidDataException("端口表不完整。");
        var rows = new List<NetworkEndpoint>();
        for (var i = 0; i < count; i++)
        {
            var row = span.Slice(4 + i * stride, stride);
            var localAddress = v6 ? new IPAddress(row[..16], ReadU32(row, 16)) : new IPAddress(row.Slice(udp ? 0 : 4, 4));
            var localPort = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(v6 ? 20 : udp ? 4 : 8, 2));
            var remote = "—"; var state = "UDP 绑定";
            if (!udp)
            {
                var tcpState = (TcpState)ReadU32(row, v6 ? 48 : 0);
                state = tcpState == TcpState.Listen ? "监听" : tcpState == TcpState.Established ? "已连接" : tcpState.ToString();
                if (tcpState != TcpState.Listen)
                {
                    var address = v6 ? new IPAddress(row.Slice(24, 16), ReadU32(row, 40)) : new IPAddress(row.Slice(12, 4));
                    remote = new IPEndPoint(address, BinaryPrimitives.ReadUInt16BigEndian(row.Slice(v6 ? 44 : 16, 2))).ToString();
                }
            }
            var pid = ReadU32(row, udp ? v6 ? 24 : 8 : v6 ? 52 : 20);
            if (pid > int.MaxValue) throw new InvalidDataException("端口进程标识无效。");
            rows.Add(new((udp ? "UDP" : "TCP") + (v6 ? "v6" : "v4"), new IPEndPoint(localAddress, localPort).ToString(),
                localPort, remote, state, (int)pid, ""));
        }
        return rows;
    }
    private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
}
