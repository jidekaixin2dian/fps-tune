using System.Diagnostics;
using System.Net.NetworkInformation;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record BackgroundProcessSample(int Pid, string Name, double CpuPercent, double? DiskBytesPerSecond);

public sealed record BackgroundCheckResult(
    IReadOnlyList<BackgroundProcessSample> TopCpu,
    IReadOnlyList<BackgroundProcessSample> TopDisk,
    double NetworkBytesPerSecond,
    double SampleSeconds,
    int ProcessCount,
    int Unavailable);

/// <summary>
/// 只读后台资源采样：固定窗口内进程 CPU/磁盘增量 + 系统网络吞吐。
/// 不结束进程、不改任何设置；受保护或已退出进程按"无法读取"如实计数。
/// </summary>
internal static class BackgroundResourceCheck
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private const double CpuNoiseFloorPercent = 0.1;   // 低于单核 0.1% 视为无活动
    private const double DiskNoiseFloorBytesPerSecond = 1024;

    internal static Func<TimeSpan, CancellationToken, Task<double>>? DelayOverride { get; set; }
    internal static Func<IReadOnlyList<Sample>>? SnapshotOverride { get; set; }
    internal static Func<ulong>? NetworkTotalOverride { get; set; }

    internal readonly record struct Sample(int Pid, string Name, TimeSpan TotalCpu, ulong? IoBytes);

    public static async Task<BackgroundCheckResult> RunAsync(CancellationToken cancellation)
    {
        var (first, firstUnavailable) = Capture();
        var networkFirst = NetworkTotal();
        double seconds;
        if (DelayOverride is not null) seconds = await DelayOverride(Window, cancellation);
        else
        {
            var watch = Stopwatch.StartNew();
            await Task.Delay(Window, cancellation);
            watch.Stop();
            seconds = watch.Elapsed.TotalSeconds;
        }
        cancellation.ThrowIfCancellationRequested();
        var (second, secondUnavailable) = Capture();
        var network = Math.Max(0, NetworkTotal() - networkFirst) / seconds;

        var samples = new List<BackgroundProcessSample>();
        foreach (var after in second.Values)
        {
            // 窗口内新出现的进程没有完整前后快照，不计入（避免半窗口高估）。
            if (!first.TryGetValue(after.Pid, out var before)) continue;
            var cpu = Math.Max(0, (after.TotalCpu - before.TotalCpu).TotalSeconds) / (seconds * Environment.ProcessorCount) * 100;
            double? disk = after.IoBytes is { } ioAfter && before.IoBytes is { } ioBefore
                ? Math.Max(0, ioAfter - ioBefore) / seconds
                : null;
            samples.Add(new(after.Pid, after.Name, cpu, disk));
        }

        var topCpu = samples.Where(s => s.CpuPercent >= CpuNoiseFloorPercent)
            .OrderByDescending(s => s.CpuPercent).Take(5).ToArray();
        var topDisk = samples.Where(s => s.DiskBytesPerSecond is { } rate && rate >= DiskNoiseFloorBytesPerSecond)
            .OrderByDescending(s => s.DiskBytesPerSecond).Take(5).ToArray();
        return new(topCpu, topDisk, network, seconds, second.Count, Math.Max(firstUnavailable, secondUnavailable));
    }

    private static (Dictionary<int, Sample> Live, int Unavailable) Capture()
    {
        if (SnapshotOverride is not null)
        {
            var live = new Dictionary<int, Sample>();
            foreach (var sample in SnapshotOverride())
                live.TryAdd(sample.Pid, sample);
            return (live, 0);
        }
        var result = new Dictionary<int, Sample>();
        var unavailable = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id <= 4) continue; // Idle/System 的 CPU 是内核空闲账目，进入榜单只会误导
                TimeSpan cpu;
                try { cpu = process.TotalProcessorTime; }
                catch { unavailable++; continue; }
                ulong? io = null;
                try { io = NativeProcessIo.TotalBytes(process.Id); }
                catch { io = null; }
                result.TryAdd(process.Id, new Sample(process.Id, process.ProcessName, cpu, io));
            }
            catch { unavailable++; }
            finally
            {
                try { process.Dispose(); } catch { /* 已退出时忽略 */ }
            }
        }
        return (result, unavailable);
    }

    private static ulong NetworkTotal()
    {
        if (NetworkTotalOverride is not null) return NetworkTotalOverride();
        ulong total = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var stats = nic.GetIPStatistics();
                total += (ulong)stats.BytesSent + (ulong)stats.BytesReceived;
            }
            catch { /* 个别网卡统计不可读，跳过该接口 */ }
        }
        return total;
    }
}
