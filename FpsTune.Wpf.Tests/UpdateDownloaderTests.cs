using System.Net;
using System.Net.Sockets;
using System.Text;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 更新安装包下载：断点续传与多源回退的真实 HTTP 行为验证。
/// 用本机回环 TCP 服务器模拟"传到一半断掉"和"第一个源一直 500"，
/// 断言第二次请求真的带 Range、断点字节被拼回来、以及候选源按顺序切换。
/// </summary>
public class UpdateDownloaderTests : IDisposable
{
    private static readonly byte[] Payload = Enumerable.Range(0, 40000).Select(i => (byte)(i * 7 % 251)).ToArray();

    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _servers = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpstune-update-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateDownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        UpdateMirrors.CandidatesOverride = null;
        UpdateMirrors.SourceOverride = null;
        foreach (var listener in _listeners) { try { listener.Stop(); } catch { } }
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Resumes_from_the_partial_file_when_the_connection_drops_midway()
    {
        var ranges = new List<string?>();
        var dropAt = Payload.Length / 2;
        var port = Serve((index, rangeHeader) =>
        {
            ranges.Add(rangeHeader);
            var start = ParseRangeStart(rangeHeader);
            // 第一次不给 Range：整段返回但只写一半就断开，模拟国内常见的传输中断。
            if (start == 0 && index == 0) return Response(200, Payload, null, truncateAfter: dropAt);
            return Response(206, Payload[start..], $"bytes {start}-{Payload.Length - 1}/{Payload.Length}");
        });

        var target = Path.Combine(_dir, "setup.exe");
        UseSource(port);
        await UpdateService.DownloadPackageAsync($"http://127.0.0.1:{port}/pkg", target, null, CancellationToken.None);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.True(ranges.Count >= 2, "应当至少请求两次（断线后再续传）");
        Assert.Equal($"bytes={dropAt}-", ranges[1]);
    }

    [Fact]
    public async Task Falls_back_to_the_next_source_when_the_first_keeps_failing()
    {
        var attemptsOnDead = 0;
        var deadPort = Serve((_, _) => { attemptsOnDead++; return Response(500, [], null); });
        var goodPort = Serve((_, rangeHeader) =>
        {
            var start = ParseRangeStart(rangeHeader);
            return start == 0
                ? Response(200, Payload, null)
                : Response(206, Payload[start..], $"bytes {start}-{Payload.Length - 1}/{Payload.Length}");
        });

        var target = Path.Combine(_dir, "setup2.exe");
        UpdateMirrors.CandidatesOverride = _ => [$"http://127.0.0.1:{deadPort}/pkg", $"http://127.0.0.1:{goodPort}/pkg"];
        UpdateMirrors.SourceOverride = _ => true;
        await UpdateService.DownloadPackageAsync($"http://127.0.0.1:{deadPort}/pkg", target, null, CancellationToken.None);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(3, attemptsOnDead);
    }

    [Fact]
    public async Task Rejects_a_source_that_is_not_on_the_allow_list()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadPackageAsync(
            "http://127.0.0.1:1/pkg", Path.Combine(_dir, "nope.exe"), null, CancellationToken.None));
    }

    private void UseSource(int port)
    {
        UpdateMirrors.CandidatesOverride = _ => [$"http://127.0.0.1:{port}/pkg"];
        UpdateMirrors.SourceOverride = _ => true;
    }

    private static int ParseRangeStart(string? rangeHeader)
        => rangeHeader is not null && rangeHeader.StartsWith("bytes=", StringComparison.Ordinal)
            ? int.TryParse(rangeHeader["bytes=".Length..].TrimEnd('-'), out var start) ? start : 0
            : 0;

    private static (int Status, byte[] Body, string? ContentRange, int? TruncateAfter) Response(
        int status, byte[] body, string? contentRange, int? truncateAfter = null)
        => (status, body, contentRange, truncateAfter);

    private int Serve(Func<int, string?, (int Status, byte[] Body, string? ContentRange, int? TruncateAfter)> handler)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listeners.Add(listener);
        var index = -1;
        _servers.Add(Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using var _client = client;
                    using var stream = client.GetStream();
                    var request = await ReadHead(stream);
                    if (request is null) return;
                    var rangeHeader = request.Value.Range;
                    var attempt = Interlocked.Increment(ref index);
                    var (status, body, contentRange, truncateAfter) = handler(attempt, rangeHeader);
                    var head = new StringBuilder()
                        .Append(status == 206 ? "HTTP/1.1 206 Partial Content\r\n" : $"HTTP/1.1 {status} {StatusText(status)}\r\n")
                        .Append("Accept-Ranges: bytes\r\n")
                        .Append("Connection: close\r\n");
                    if (contentRange is not null) head.Append("Content-Range: ").Append(contentRange).Append("\r\n");
                    head.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
                    try
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                        var payload = truncateAfter is { } cut ? body[..cut] : body;
                        await stream.WriteAsync(payload);
                        await stream.FlushAsync();
                    }
                    catch { /* 客户端提前断开是预期路径之一 */ }
                });
            }
        }));
        return port;
    }

    private readonly record struct Request(string? Range);

    private static async Task<Request?> ReadHead(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read <= 0) return null;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;
            string? range = null;
            foreach (var line in text.Split("\r\n"))
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
            return new Request(range);
        }
        return null;
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        _ => "Internal Server Error",
    };
}
