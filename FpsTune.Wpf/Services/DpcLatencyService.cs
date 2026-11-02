using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FpsTune.Wpf.Services;

public sealed record DpcDriverSample(
    string Module, int DpcCount, int IsrCount, double DpcTotalMs, double DpcMaxMs, double IsrTotalMs, double IsrMaxMs)
{
    public double CombinedTotalMs => Math.Round(DpcTotalMs + IsrTotalMs, 2);
    public double CombinedMaxMs => Math.Round(Math.Max(DpcMaxMs, IsrMaxMs), 2);
    public int CombinedCount => DpcCount + IsrCount;
}

public sealed record DpcLatencyReport(
    bool Ok, DateTimeOffset MeasuredAt, int DurationSeconds, int ProcessorCount,
    double DpcTotalMs, double DpcMaxMs, double IsrTotalMs, double IsrMaxMs,
    int DpcCount, int IsrCount, double DpcCpuPercent, double IsrCpuPercent,
    IReadOnlyList<DpcDriverSample> Drivers, string Verdict, string Severity, string? Error)
{
    public static DpcLatencyReport Failed(string error) => new(false, DateTimeOffset.Now, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<DpcDriverSample>(), Str.T("Str.DpcVerdictUnknown"), "unknown", error);
}

/// <summary>
/// 只读 DPC/ISR 采样：内核 ETW real-time session，不加载驱动、不改系统、不留后台服务。
/// 需要管理员；每次采样结束即销毁 session。驱动归属通过 SystemModuleInformation 地址区间映射。
/// </summary>
public static class DpcLatencyService
{
    public const int DefaultSeconds = 30;
    public const int MinSeconds = 5;
    public const int MaxSeconds = 120;

    public static async Task<DpcLatencyReport> MeasureAsync(int seconds, CancellationToken token = default)
    {
        if (!AdminHelper.IsAdministrator())
            return DpcLatencyReport.Failed(Str.T("Str.DpcNeedAdmin"));
        if (seconds is < MinSeconds or > MaxSeconds)
            return DpcLatencyReport.Failed(Str.T("Str.DpcDurationInvalid", MinSeconds, MaxSeconds));
        return await Task.Run(() => Measure(seconds, token), token).ConfigureAwait(false);
    }

    internal static DpcLatencyReport Measure(int seconds, CancellationToken token = default, bool? isAdmin = null)
    {
        if (isAdmin == false || (isAdmin is null && !AdminHelper.IsAdministrator()))
            return DpcLatencyReport.Failed(Str.T("Str.DpcNeedAdmin"));
        if (seconds is < MinSeconds or > MaxSeconds)
            return DpcLatencyReport.Failed(Str.T("Str.DpcDurationInvalid", MinSeconds, MaxSeconds));

        var modules = KernelModuleMap.Capture();
        var samples = new SampleCollector(modules);
        var sessionName = "FpsTune-Dpc-" + Guid.NewGuid().ToString("N")[..8];
        TraceEventSession? session = null;
        try
        {
            session = new TraceEventSession(sessionName) { StopOnDispose = true };
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DeferedProcedureCalls
                | KernelTraceEventParser.Keywords.Interrupt
                | KernelTraceEventParser.Keywords.ImageLoad,
                KernelTraceEventParser.Keywords.None);

            var kernel = session.Source.Kernel;
            kernel.PerfInfoDPC += data => samples.AddDpc(data.Routine, data.ElapsedTimeMSec);
            kernel.PerfInfoThreadedDPC += data => samples.AddDpc(data.Routine, data.ElapsedTimeMSec);
            kernel.PerfInfoTimerDPC += data => samples.AddDpc(data.Routine, data.ElapsedTimeMSec);
            kernel.PerfInfoISR += data => samples.AddIsr(data.Routine, data.ElapsedTimeMSec);
            // NtQuery 在部分 Windows 版本会把镜像基址清零；ETW ImageLoad 是带真实基址的权威来源。
            kernel.ImageLoadGroup += data => samples.AddImage(data.ImageBase,
                unchecked((uint)data.ImageSize), data.FileName);

            Exception? processingError = null;
            var processor = new Thread(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) { processingError = ex; }
            })
            { IsBackground = true, Name = "FpsTune-Dpc-Etw" };
            processor.Start();

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100))) break;
            }
            session.Stop();
            if (!processor.Join(TimeSpan.FromSeconds(8)))
                processingError ??= new TimeoutException("ETW processor did not stop");
            if (processingError is not null) return DpcLatencyReport.Failed(PrivacyScrub.Sanitize(processingError.Message));

            var elapsed = Math.Max(1, (int)Math.Round(deadline.Elapsed.TotalSeconds));
            return samples.Build(seconds, elapsed);
        }
        catch (Exception ex)
        {
            return DpcLatencyReport.Failed(PrivacyScrub.Sanitize(ex.Message));
        }
        finally
        {
            try { session?.Dispose(); } catch { /* 采样失败时 session 清理错误不覆盖原始错误 */ }
        }
    }

    /// <summary>通过独立管理员进程采样，避免重启整个 GUI；结果走临时 JSON 文件。</summary>
    public static async Task<DpcLatencyReport> MeasureElevatedAsync(int seconds, CancellationToken token = default)
    {
        if (AdminHelper.IsAdministrator()) return await MeasureAsync(seconds, token).ConfigureAwait(false);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return DpcLatencyReport.Failed(Str.T("Str.DpcCannotStart"));
        var outPath = Path.Combine(UserDataPaths.Root, "dpc-latency-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-DpcLatency -Duration {seconds} -Out \"{outPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process is null) return DpcLatencyReport.Failed(Str.T("Str.DpcCannotStart"));
            var deadline = DateTime.UtcNow.AddSeconds(seconds + 45);
            while (DateTime.UtcNow < deadline)
            {
                if (token.IsCancellationRequested) return DpcLatencyReport.Failed(Str.T("Str.DpcCancelled"));
                if (File.Exists(outPath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(outPath, token).ConfigureAwait(false);
                        var report = JsonSerializer.Deserialize<DpcLatencyReport>(json);
                        if (report is not null) return report;
                    }
                    catch (IOException) { /* 文件仍在写入，下一轮再读 */ }
                }
                if (process.HasExited)
                {
                    if (File.Exists(outPath))
                    {
                        var json = await File.ReadAllTextAsync(outPath, token).ConfigureAwait(false);
                        return JsonSerializer.Deserialize<DpcLatencyReport>(json)
                            ?? DpcLatencyReport.Failed(Str.T("Str.DpcReadFailed"));
                    }
                    return DpcLatencyReport.Failed(Str.T("Str.DpcAdminProcessExited", process.ExitCode));
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            return DpcLatencyReport.Failed(Str.T("Str.DpcTimeout"));
        }
        catch (OperationCanceledException) { return DpcLatencyReport.Failed(Str.T("Str.DpcCancelled")); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return DpcLatencyReport.Failed(Str.T("Str.DpcElevationCancelled"));
        }
        catch (Exception ex) { return DpcLatencyReport.Failed(PrivacyScrub.Sanitize(ex.Message)); }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }

    internal sealed class SampleCollector
    {
        private readonly List<KernelModule> _modules;
        private readonly List<RawSample> _samples = new();
        private readonly object _gate = new();

        public SampleCollector(IReadOnlyList<KernelModule> modules)
            => _modules = modules.Where(m => m.Base != 0 && m.Size > 0).ToList();

        public void AddDpc(ulong routine, double ms) => Add(routine, ms, false);
        public void AddIsr(ulong routine, double ms) => Add(routine, ms, true);

        public void AddImage(ulong imageBase, uint imageSize, string? fileName)
        {
            if (imageBase == 0 || imageSize == 0 || string.IsNullOrWhiteSpace(fileName)) return;
            lock (_gate)
            {
                if (_modules.Any(m => m.Base == imageBase)) return;
                _modules.Add(new KernelModule(imageBase, imageSize, Path.GetFileName(fileName)));
            }
        }

        private void Add(ulong routine, double ms, bool isr)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0) return;
            lock (_gate) _samples.Add(new RawSample(routine, ms, isr));
        }

        public DpcLatencyReport Build(int requestedSeconds, int elapsedSeconds)
        {
            lock (_gate)
            {
                // 采样结束后统一归属，避免 DPC 先于 ImageLoad 到达时被永久记成 Unknown。
                var modules = _modules.OrderByDescending(m => m.Base).ToList();
                var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
                var dpcCount = 0;
                var isrCount = 0;
                var dpcTotal = 0.0;
                var dpcMax = 0.0;
                var isrTotal = 0.0;
                var isrMax = 0.0;
                foreach (var sample in _samples)
                {
                    var module = KernelModuleMap.Resolve(modules, sample.Routine);
                    if (!buckets.TryGetValue(module, out var bucket))
                        buckets[module] = bucket = new Bucket();
                    if (sample.Isr)
                    {
                        isrCount++;
                        isrTotal += sample.Ms;
                        isrMax = Math.Max(isrMax, sample.Ms);
                        bucket.IsrCount++;
                        bucket.IsrTotalMs += sample.Ms;
                        bucket.IsrMaxMs = Math.Max(bucket.IsrMaxMs, sample.Ms);
                    }
                    else
                    {
                        dpcCount++;
                        dpcTotal += sample.Ms;
                        dpcMax = Math.Max(dpcMax, sample.Ms);
                        bucket.DpcCount++;
                        bucket.DpcTotalMs += sample.Ms;
                        bucket.DpcMaxMs = Math.Max(bucket.DpcMaxMs, sample.Ms);
                    }
                }
                var drivers = buckets
                    .Select(pair => new DpcDriverSample(pair.Key, pair.Value.DpcCount, pair.Value.IsrCount,
                        Round(pair.Value.DpcTotalMs), Round(pair.Value.DpcMaxMs),
                        Round(pair.Value.IsrTotalMs), Round(pair.Value.IsrMaxMs)))
                    .OrderByDescending(d => d.DpcTotalMs + d.IsrTotalMs)
                    .ThenByDescending(d => d.DpcMaxMs)
                    .Take(12).ToList();
                var cpuWindowMs = Math.Max(1, elapsedSeconds) * 1000.0 * Math.Max(1, Environment.ProcessorCount);
                var dpcCpu = Round(dpcTotal / cpuWindowMs * 100);
                var isrCpu = Round(isrTotal / cpuWindowMs * 100);
                var (verdict, severity) = DpcVerdict(dpcMax, dpcCpu, dpcTotal);
                return new DpcLatencyReport(dpcCount + isrCount > 0, DateTimeOffset.Now, requestedSeconds,
                    Environment.ProcessorCount, Round(dpcTotal), Round(dpcMax), Round(isrTotal), Round(isrMax),
                    dpcCount, isrCount, dpcCpu, isrCpu, drivers, verdict, severity,
                    dpcCount + isrCount > 0 ? null : Str.T("Str.DpcNoSamples"));
            }
        }

        private readonly record struct RawSample(ulong Routine, double Ms, bool Isr);

        private static (string Text, string Severity) DpcVerdict(double maxMs, double cpuPercent, double totalMs)
        {
            _ = totalMs; // 累计值随采样长度增长，不参与阈值判断，避免 30 秒正常样本被误报。
            return maxMs >= 10 || cpuPercent >= 8 ? (Str.T("Str.DpcVerdictHigh"), "high")
                : maxMs >= 2 || cpuPercent >= 3 ? (Str.T("Str.DpcVerdictWatch"), "watch")
                : (Str.T("Str.DpcVerdictGood"), "good");
        }
        private static double Round(double value) => Math.Round(value, 2);
        private sealed class Bucket
        {
            public int DpcCount; public int IsrCount;
            public double DpcTotalMs; public double DpcMaxMs;
            public double IsrTotalMs; public double IsrMaxMs;
        }
    }

    internal readonly record struct KernelModule(ulong Base, uint Size, string Name);

    internal static class KernelModuleMap
    {
        private const int SystemModuleInformation = 11;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        public static IReadOnlyList<KernelModule> Capture()
        {
            try
            {
                var size = 0;
                var status = NtQuerySystemInformation(SystemModuleInformation, IntPtr.Zero, 0, out size);
                if (status != StatusInfoLengthMismatch && status != 0) return Array.Empty<KernelModule>();
                size = Math.Max(size, 64 * 1024);
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        status = NtQuerySystemInformation(SystemModuleInformation, buffer, size, out var needed);
                        if (status == StatusInfoLengthMismatch) { size = Math.Max(needed, size * 2); continue; }
                        if (status != 0) return Array.Empty<KernelModule>();
                        var count = Marshal.ReadInt32(buffer);
                        var list = new List<KernelModule>(count);
                        var first = IntPtr.Add(buffer, IntPtr.Size);
                        // RTL_PROCESS_MODULE_INFORMATION: 指针字段在前，不能依赖整个结构体的
                        // 托管布局（x64 下曾把 ImageBase 读成 0）。按公开偏移逐个字段取。
                        var pointerBytes = IntPtr.Size;
                        var stride = 3 * pointerBytes + 16 + 256;
                        for (var i = 0; i < count; i++)
                        {
                            var info = IntPtr.Add(first, i * stride);
                            var imageBase = unchecked((ulong)Marshal.ReadIntPtr(info, 2 * pointerBytes).ToInt64());
                            var imageSize = unchecked((uint)Marshal.ReadInt32(info, 3 * pointerBytes));
                            var nameOffset = Marshal.ReadInt16(info, 3 * pointerBytes + 14) & 0xFFFF;
                            var path = new byte[256];
                            Marshal.Copy(IntPtr.Add(info, 3 * pointerBytes + 16), path, 0, path.Length);
                            nameOffset = Math.Min(nameOffset, path.Length);
                            var end = Array.IndexOf(path, (byte)0, nameOffset);
                            if (end < 0) end = path.Length;
                            var name = Encoding.ASCII.GetString(path, nameOffset, end - nameOffset);
                            if (name.Length == 0) name = "ntoskrnl.exe";
                            list.Add(new KernelModule(imageBase, imageSize, Path.GetFileName(name)));
                        }
                        return list;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            catch { }
            return Array.Empty<KernelModule>();
        }

        public static string Resolve(IReadOnlyList<KernelModule> modules, ulong address)
        {
            if (address == 0) return "Unknown";
            foreach (var module in modules)
            {
                if (address >= module.Base && address < module.Base + module.Size)
                    return module.Name;
            }
            return "Unknown";
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
    }
}
