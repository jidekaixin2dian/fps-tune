using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>一次定量采样请求。进程给名字（dwm / 浏览器 / 游戏），可选 PID 与完整路径身份锚。</summary>
public sealed record CaptureRequest(
    string ProcessName,
    int? ProcessId = null,
    string? GamePath = null,
    int DurationSec = 60,
    string? Label = null,
    bool IncludeGpu = true,
    bool IncludeBackground = true);

/// <summary>采样期显卡读数汇总（2 s 一次 nvidia-smi 的均值 + 降频原因并集）。</summary>
public sealed record CaptureGpuSummary(
    string? Name,
    double? UtilizationPercent,
    double? MemoryPercent,
    double? TemperatureC,
    double? PowerW,
    double? ClockSmMhz,
    IReadOnlyList<string> ThrottleReasons,
    int Samples);

/// <summary>采样期整机后台负载（整机 CPU 均值）。对照期间用于标注外部负载波动。</summary>
public sealed record CaptureBackgroundSummary(double? AvgCpuPercent, int Samples);

/// <summary>一次定量采样的产物。失败时 Frames=0 且 Error/ReasonCode 说明原因；JSON 随 run.json 落盘。</summary>
public sealed record CaptureRun(
    string Id,
    DateTimeOffset StartedAt,
    int DurationSec,
    string? Label,
    int Frames,
    double AvgFps,
    double P1Low,
    double P99Ms,
    int Stutters,
    CaptureGpuSummary? Gpu,
    CaptureBackgroundSummary? Background,
    string CsvPath,
    string JsonPath,
    string? Error,
    string? ReasonCode)
{
    internal static CaptureRun Fail(CaptureRequest request, string directory, string id, string error, string reasonCode)
        => new(id, DateTimeOffset.Now, request.DurationSec, request.Label, 0, 0, 0, 0, 0,
            null, null, Path.Combine(directory, "frames.csv"), Path.Combine(directory, "run.json"), error, reasonCode);
}

/// <summary>
/// 定量采样的唯一编排入口（0.5.0）：帧时间（PresentMon）+ 显卡遥测（nvidia-smi 2 s）+ 整机后台负载
/// 共用同一起止窗口，产出 <see cref="CaptureRun"/> 并落盘（run.json + frames.csv）。
/// 操作回执写入 OperationDiagnostics（operation=capture，阶段 prepare → sample → finalize）。
/// 供前后对照（0.5.0 M2）、延迟预算（M5）与 CLI -Capture 使用；A/B 实验管线沿用既有入口。
/// </summary>
public static class CaptureOrchestrator
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    /// <summary>CaptureRun 的 JSON 形态（run.json 与 CLI -Out/-Json 输出共用，字段小驼峰）。</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 测试注入点：采样器路径 / 采样执行 / 目标进程 / 显卡遥测 / 后台采样（沿用项目既有注入习惯）。
    internal static Func<string?>? PresentMonOverride;
    internal static Func<string, int?, string, int, string, CancellationToken, Task<bool>>? SamplerOverride;
    internal static Func<string, string?, int?, Process?>? FindProcessOverride;
    internal static Func<string?>? FindFailureOverride;
    internal static Func<GpuTelemetrySnapshot>? GpuReadOverride;
    internal static Func<MetricsSampler>? MetricsFactoryOverride;

    /// <summary>采样的统一输出目录（run.json + frames.csv 的父目录）。</summary>
    internal static string CapturesRoot => Path.Combine(UserDataPaths.Root, "captures");

    public static async Task<CaptureRun> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        var id = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..6];
        var directory = Path.Combine(CapturesRoot, id);
        var csvPath = Path.Combine(directory, "frames.csv");
        var jsonPath = Path.Combine(directory, "run.json");

        if (!await RunGate.WaitAsync(0, ct).ConfigureAwait(false))
            return CaptureRun.Fail(request, directory, id, "已有一次采样正在进行，请等它结束后再试。", "busy");
        try
        {
            using var trace = OperationDiagnostics.Begin("capture",
                string.IsNullOrWhiteSpace(request.GamePath)
                    ? $"{request.ProcessName} · {request.DurationSec}s"
                    : $"{request.ProcessName} · {request.DurationSec}s · {Path.GetFileName(request.GamePath)}",
                null);
            try
            {
                var run = await RunAsync(request, id, directory, csvPath, jsonPath, trace, ct).ConfigureAwait(false);
                trace.Settle(run.Error is null ? "succeeded" : "failed");
                return run;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                trace.Fail(ex);
                return CaptureRun.Fail(request, directory, id, ex.Message, "unexpected");
            }
        }
        finally { RunGate.Release(); }
    }

    private static async Task<CaptureRun> RunAsync(CaptureRequest request, string id, string directory,
        string csvPath, string jsonPath, OperationDiagnostics.Trace trace, CancellationToken ct)
    {
        // ---- prepare ----
        trace.Stage("prepare");
        Directory.CreateDirectory(directory);

        var presentMon = PresentMonOverride is { } injected ? injected() : PresentMonLocator.Find();
        if (presentMon is null)
        {
            trace.Item("presentmon", "failed", "内置与已装采样器都不可用");
            return CaptureRun.Fail(request, directory, id,
                "没有找到可用的帧率采样器（内置采样器不可用，也没有已安装的 PresentMon）。",
                "no-sampler");
        }
        trace.Item("presentmon", "succeeded", null);

        var (target, findFailure) = FindTargetProcess(request);
        if (target is null)
        {
            trace.Item("process", "failed", "no-target");
            return CaptureRun.Fail(request, directory, id, findFailure, "no-process");
        }
        using (target)
        {
            // ---- sample ----
            trace.Stage("sample");
            var seconds = Math.Clamp(request.DurationSec, 1, 600);
            var sw = Stopwatch.StartNew();
            Task<CaptureGpuSummary?>? telemetryTask = request.IncludeGpu
                ? Task.Run(() => CollectGpuAsync(seconds, ct), ct)
                : null;
            var background = StartBackground(request, ct);
            try
            {
                string? samplerFailure = null;
                bool sampled;
                if (SamplerOverride is { } sampler)
                {
                    sampled = await sampler(presentMon, target.Id, request.ProcessName, seconds, csvPath, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    var outcome = await PresentMonSampler
                        .CaptureAsync(presentMon, target.Id, request.ProcessName, seconds, csvPath, ct)
                        .ConfigureAwait(false);
                    sampled = outcome.Sampled;
                    samplerFailure = outcome.FailureReason;
                }
                sw.Stop();
                if (!sampled)
                {
                    if (samplerFailure == "permission")
                    {
                        trace.Item("frames", "failed", "permission");
                        return CaptureRun.Fail(request, directory, id,
                            "采样被系统拒绝：PresentMon 的 ETW 会话需要管理员权限。请以管理员身份运行后重试。",
                            "no-permission");
                    }
                    trace.Item("frames", "failed", "PresentMon 未产出有效数据");
                    return CaptureRun.Fail(request, directory, id,
                        "采样没有产生有效数据（目标进程可能已退出，或采样器版本不受支持）。", "sample-failed");
                }
                trace.Item("frames", "succeeded", $"{sw.Elapsed.TotalSeconds:0}s");

                // ---- finalize ----
                trace.Stage("finalize");
                var parsed = await Task.Run(
                    () => FrameTimeStats.Parse(File.ReadLines(csvPath), ct, FrameTimeStats.MaxFrameCount),
                    ct).ConfigureAwait(false);
                if (parsed.Frame is not { } frame)
                {
                    var (error, reason) = parsed.Error switch
                    {
                        FrameParseError.UnknownColumns =>
                            ("采样器输出的列无法识别（采样器版本可能不兼容），原始数据已保留。", "incompatible-sampler"),
                        FrameParseError.TooFewFrames or FrameParseError.TooFewLines =>
                            ($"有效帧样本不足（{parsed.FrameCount} 帧），采样窗口内目标可能没有在渲染。", "parse-failed"),
                        FrameParseError.TooManyFrames =>
                            ("采样数据超出解析容量。", "parse-failed"),
                        _ => ("采样数据解析失败。", "parse-failed"),
                    };
                    trace.Item("parse", "failed", reason);
                    return CaptureRun.Fail(request, directory, id, error, reason);
                }
                trace.Item("parse", "succeeded", $"{frame.Samples} 帧");

                CaptureGpuSummary? gpu = null;
                if (request.IncludeGpu && telemetryTask is not null)
                {
                    gpu = await telemetryTask.ConfigureAwait(false);
                    trace.Item("gpu", gpu is null ? "skipped" : "succeeded", gpu is null ? null : $"{gpu.Samples} 次读数");
                }
                CaptureBackgroundSummary? backgroundSummary = null;
                if (request.IncludeBackground && background is not null)
                {
                    backgroundSummary = background.Summarize();
                    trace.Item("background", backgroundSummary is { } bg && bg.Samples > 0 ? "succeeded" : "skipped",
                        backgroundSummary is { } summary && summary.Samples > 0 ? $"{summary.Samples} 次读数" : null);
                }

                var run = new CaptureRun(id, DateTimeOffset.Now, seconds, request.Label,
                    frame.Samples, frame.AvgFps, frame.P1Low, frame.P99Ms, frame.Stutters,
                    gpu, backgroundSummary, csvPath, jsonPath, null, null);
                AtomicFile.WriteAllTextDurable(jsonPath, JsonSerializer.Serialize(run, JsonOpts), new UTF8Encoding(false));
                return run;
            }
            finally
            {
                background?.Dispose();
            }
        }
    }

    /// <summary>按 PID / 进程名定位目标。唯一同名才按名采（多实例不确定目标），路径锚给定时校验身份。</summary>
    private static (Process? Process, string Failure) FindTargetProcess(CaptureRequest request)
    {
        if (FindProcessOverride is { } injected)
            return (injected(request.ProcessName, request.GamePath, request.ProcessId),
                FindFailureOverride?.Invoke() ?? $"没有找到正在运行的 {request.ProcessName}。");
        try
        {
            if (request.ProcessId is { } pid)
            {
                var byPid = Process.GetProcessById(pid);
                if (string.IsNullOrWhiteSpace(request.GamePath) || ProcessIdentity.MatchesPath(byPid, request.GamePath))
                    return (byPid, "");
                byPid.Dispose();
                return (null, "指定 PID 的进程与定位的游戏不一致。");
            }
            var candidates = Process.GetProcessesByName(request.ProcessName);
            if (candidates.Length == 0)
                return (null, $"没有找到正在运行的 {request.ProcessName}，请先打开目标程序。");
            if (candidates.Length > 1 && !string.IsNullOrWhiteSpace(request.GamePath))
            {
                // 有路径锚时按身份挑：只接受完整路径一致的那个进程。
                foreach (var candidate in candidates)
                {
                    if (ProcessIdentity.MatchesPath(candidate, request.GamePath))
                        return (candidate, "");
                    candidate.Dispose();
                }
                return (null, $"找到 {candidates.Length} 个 {request.ProcessName}，但没有一个与定位的游戏路径一致。");
            }
            if (candidates.Length > 1)
            {
                foreach (var candidate in candidates) candidate.Dispose();
                return (null, $"同名进程有 {candidates.Length} 个，无法确定采样目标；请关闭多余实例后重试。");
            }
            return (candidates[0], "");
        }
        catch (Exception ex)
        {
            return (null, "定位采样目标失败：" + ex.Message);
        }
    }

    /// <summary>每 2 s 查一次显卡遥测（GpuTelemetryService 自带 2 s 缓存窗口），与帧时间窗口对齐。</summary>
    private static async Task<CaptureGpuSummary?> CollectGpuAsync(int seconds, CancellationToken ct)
    {
        var readings = new List<GpuReading>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = GpuReadOverride?.Invoke() ?? GpuTelemetryService.Read();
            if (snapshot.Reading is { } reading)
                readings.Add(reading);
            var remaining = seconds - sw.Elapsed.TotalSeconds;
            if (remaining <= 0) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2, remaining)), ct).ConfigureAwait(false);
        }
        if (readings.Count == 0)
            return null;
        // 窗口重叠校验：期望约 seconds/2 次读数，少于四分之一说明遥测大面积缺失，不给均值以免误导。
        if (readings.Count < Math.Max(1, seconds / 8))
            return null;
        double? Avg(Func<GpuReading, double?> pick)
        {
            var values = readings.Select(pick).Where(v => v is { }).Select(v => v!.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }
        var names = readings.Select(r => r.Name).Where(n => n.Length > 0).ToList();
        return new CaptureGpuSummary(
            names.Count > 0 ? names[0] : null,
            Avg(r => r.UtilizationPercent),
            Avg(r => r.MemoryPercent),
            Avg(r => r.TemperatureC),
            Avg(r => r.PowerW),
            Avg(r => r.ClockSmMhz),
            readings.SelectMany(GpuTelemetryService.ThrottleReasons).Distinct().ToList(),
            readings.Count);
    }

    /// <summary>
    /// 后台负载采样循环。MetricsSampler 自带的定时器依赖 WPF Dispatcher（CLI / 后台线程没有消息泵），
    /// 这里自己按 2 s 周期驱动 <see cref="MetricsSampler.SampleOnce"/>，样本经 Publish 直接收录 Buffer。
    /// </summary>
    private sealed class BackgroundLoop : IDisposable
    {
        private readonly MetricsSampler _sampler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private bool _disposed;

        public BackgroundLoop(MetricsSampler sampler, CancellationToken ct)
        {
            _sampler = sampler;
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try { _sampler.SampleOnce(); }
                    catch { /* 后台负载是辅助标注，单次读失败继续。 */ }
                    try { await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, CancellationToken.None);
        }

        public CaptureBackgroundSummary Summarize()
        {
            var cpu = _sampler.Buffer.Select(s => s.CpuPercent).Where(v => v is { }).Select(v => v!.Value).ToList();
            return new CaptureBackgroundSummary(cpu.Count == 0 ? null : Math.Round(cpu.Average(), 1), cpu.Count);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch { /* 循环已因取消退出或尚未真正启动。 */ }
            _cts.Dispose();
            _sampler.Dispose();
        }
    }

    private static BackgroundLoop? StartBackground(CaptureRequest request, CancellationToken ct)
    {
        if (!request.IncludeBackground)
            return null;
        try
        {
            var sampler = MetricsFactoryOverride?.Invoke() ?? new MetricsSampler(TimeSpan.FromSeconds(2), capacity: 128);
            return new BackgroundLoop(sampler, ct);
        }
        catch
        {
            return null; // 后台负载是辅助标注，拿不到不阻塞采样。
        }
    }
}
