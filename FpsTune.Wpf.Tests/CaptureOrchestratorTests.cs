using System.Diagnostics;
using System.Text.Json;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// CaptureOrchestrator：统一采样入口的产物落盘、失败分类（采样器缺失 / 目标缺失 / 采样失败 /
/// 列名不兼容 / 帧不足）、并发闸、遥测开关，以及 CLI -Capture 的参数校验。
/// 采样执行与目标进程全部注入，真机三场景在部署验收环节另行覆盖。
/// UserDataPaths.RootOverride 是全局静态：与其他设置它的测试类共享串行 collection。
/// </summary>
[Collection("BackupService serial")]
public sealed class CaptureOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fpstune-capture-tests-" + Guid.NewGuid().ToString("N"));

    public CaptureOrchestratorTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        UserDataPaths.RootOverride = null;
        CaptureOrchestrator.PresentMonOverride = null;
        CaptureOrchestrator.SamplerOverride = null;
        CaptureOrchestrator.FindProcessOverride = null;
        CaptureOrchestrator.FindFailureOverride = null;
        CaptureOrchestrator.GpuReadOverride = null;
        CaptureOrchestrator.MetricsFactoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static GpuTelemetrySnapshot GpuSnapshot() => new(
        new GpuReading("Test GPU", 55, 4096, 8192, 60, 120, 240, 1800, 5000, 50, 0),
        GpuVerdict.GpuBusy, DateTimeOffset.Now, null);

    /// <summary>写一份最小可解析的 PresentMon CSV（msBetweenPresents 列，60 FPS）。</summary>
    private static void WriteFrameCsv(string path, int frames = 90, string header = "msBetweenPresents,msInPresentAPI")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string> { header };
        for (var i = 0; i < frames; i++)
            lines.Add((1000d / 60).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ",1.0");
        File.WriteAllLines(path, lines);
    }

    private void UseDefaultInjection()
    {
        // 注入即接管：不触发真实解压与真实进程查找。
        CaptureOrchestrator.PresentMonOverride = () => Path.Combine(_root, "pm.exe");
        CaptureOrchestrator.FindProcessOverride = (_, _, _) => Process.GetCurrentProcess();
    }

    private void UseFrameSampler(int frames = 90, string header = "msBetweenPresents,msInPresentAPI") =>
        CaptureOrchestrator.SamplerOverride = (_, _, _, _, csv, _) =>
        {
            WriteFrameCsv(csv, frames, header);
            return Task.FromResult(true);
        };

    [Fact]
    public async Task Capture_success_persists_run_json_and_roundtrips()
    {
        UseDefaultInjection();
        UseFrameSampler();
        CaptureOrchestrator.GpuReadOverride = GpuSnapshot;
        CaptureOrchestrator.MetricsFactoryOverride = () =>
            new MetricsSampler(TimeSpan.FromMilliseconds(20), capacity: 64,
                readOverride: () => new MetricSample(DateTime.Now, 12.5, null, null, null, null));

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1, Label: "idle"), CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Null(run.ReasonCode);
        Assert.Equal(90, run.Frames);
        Assert.Equal(60, run.AvgFps, 1);
        Assert.StartsWith("capture-", run.Id);
        Assert.Equal("idle", run.Label);
        Assert.EndsWith("frames.csv", run.CsvPath, StringComparison.Ordinal);
        Assert.True(File.Exists(run.CsvPath), "原始帧 CSV 应落盘");
        Assert.True(File.Exists(run.JsonPath), "run.json 应落盘");
        Assert.NotNull(run.Gpu);
        Assert.True(run.Gpu!.Samples >= 1, "采样窗口内应有显卡读数");
        Assert.Equal("Test GPU", run.Gpu.Name);
        Assert.Equal(55, run.Gpu.UtilizationPercent);
        Assert.NotNull(run.Background);
        Assert.True(run.Background!.Samples > 0, "后台负载应有读数");
        Assert.Equal(12.5, run.Background.AvgCpuPercent);

        var restored = JsonSerializer.Deserialize<CaptureRun>(
            File.ReadAllText(run.JsonPath), CaptureOrchestrator.JsonOpts);
        Assert.NotNull(restored);
        Assert.Equal(run.Id, restored!.Id);
        Assert.Equal(run.Frames, restored.Frames);
        Assert.Equal(run.AvgFps, restored.AvgFps);
        Assert.Equal(run.Label, restored.Label);
        Assert.Equal(run.Gpu.Samples, restored.Gpu!.Samples);
    }

    [Fact]
    public async Task Capture_without_sampler_fails_with_clear_reason()
    {
        CaptureOrchestrator.PresentMonOverride = () => null;

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);

        Assert.Equal("no-sampler", run.ReasonCode);
        Assert.NotNull(run.Error);
        Assert.Contains("采样器", run.Error);
        Assert.Equal(0, run.Frames);
        Assert.False(File.Exists(run.JsonPath), "失败路径不落 run.json（诊断走 operations 回执）");
    }

    [Fact]
    public async Task Capture_without_target_process_reports_reason()
    {
        CaptureOrchestrator.PresentMonOverride = () => Path.Combine(_root, "pm.exe");
        CaptureOrchestrator.FindProcessOverride = (_, _, _) => null;
        CaptureOrchestrator.FindFailureOverride = () => "没有找到正在运行的 test-target。";

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);

        Assert.Equal("no-process", run.ReasonCode);
        Assert.Equal("没有找到正在运行的 test-target。", run.Error);
    }

    [Fact]
    public async Task Capture_sampler_failure_is_classified()
    {
        UseDefaultInjection();
        CaptureOrchestrator.PresentMonOverride = () => Path.Combine(_root, "pm.exe");
        CaptureOrchestrator.SamplerOverride = (_, _, _, _, _, _) => Task.FromResult(false);

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);

        Assert.Equal("sample-failed", run.ReasonCode);
        Assert.NotNull(run.Error);
    }

    [Fact]
    public async Task Capture_unknown_columns_report_sampler_incompatible()
    {
        UseDefaultInjection();
        CaptureOrchestrator.PresentMonOverride = () => Path.Combine(_root, "pm.exe");
        UseFrameSampler(header: "foo,bar");

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);

        Assert.Equal("incompatible-sampler", run.ReasonCode);
        Assert.Contains("不兼容", run.Error);
        Assert.True(File.Exists(run.CsvPath), "列名不兼容时原始 CSV 应保留供排查");
    }

    [Fact]
    public async Task Capture_too_few_frames_is_parse_failure()
    {
        UseDefaultInjection();
        CaptureOrchestrator.PresentMonOverride = () => Path.Combine(_root, "pm.exe");
        UseFrameSampler(frames: 3);

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);

        Assert.Equal("parse-failed", run.ReasonCode);
        Assert.Contains("不足", run.Error);
    }

    [Fact]
    public async Task Capture_rejects_second_concurrent_capture()
    {
        UseDefaultInjection();
        var inGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureOrchestrator.PresentMonOverride = () => Path.Combine(_root, "pm.exe");
        CaptureOrchestrator.SamplerOverride = async (_, _, _, _, csv, _) =>
        {
            WriteFrameCsv(csv);
            inGate.SetResult();
            await release.Task;
            return true;
        };

        var first = CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);
        await inGate.Task; // 第一个已持闸并进入采样
        var second = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);
        release.SetResult();
        var firstResult = await first;

        Assert.Equal("busy", second.ReasonCode);
        Assert.Null(firstResult.Error);
    }

    [Fact]
    public async Task Capture_gpu_disabled_skips_telemetry()
    {
        UseDefaultInjection();
        UseFrameSampler();
        var gpuReads = 0;
        CaptureOrchestrator.GpuReadOverride = () => { gpuReads++; return GpuSnapshot(); };

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1, IncludeGpu: false), CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Null(run.Gpu);
        Assert.Equal(0, gpuReads);
        Assert.NotNull(run.Background);
    }

    [Fact]
    public async Task Capture_gpu_with_no_readings_is_null_not_zero_mean()
    {
        UseDefaultInjection();
        UseFrameSampler();
        CaptureOrchestrator.GpuReadOverride = () => new GpuTelemetrySnapshot(
            null, GpuVerdict.Unavailable, DateTimeOffset.Now, "no nvidia-smi");

        var run = await CaptureOrchestrator.CaptureAsync(
            new CaptureRequest("test-target", DurationSec: 1), CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Null(run.Gpu);
    }

    [Fact]
    public async Task PresentMonSampler_returns_false_when_executable_is_untrusted()
    {
        // cmd.exe 有 Microsoft 签名但不在 Intel/NVIDIA 白名单：OpenVerified 拒绝 → 全部参数组合失败。
        var csv = Path.Combine(_root, "out", "frames.csv");
        var outcome = await PresentMonSampler.CaptureAsync(
            Environment.ProcessPath is { } self ? self : "cmd.exe", null, "test-target", 1, csv, CancellationToken.None);

        Assert.False(outcome.Sampled);
        Assert.Null(outcome.FailureReason); // 验签失败不属于 ETW 权限拒绝
        Assert.False(File.Exists(csv));
    }
}

/// <summary>CLI -Capture 的参数校验（不触发真实采样）。</summary>
public sealed class CaptureCliTests
{
    [Fact]
    public void Capture_requires_target()
    {
        using var writer = new StringWriter();
        var exit = CliHost.Run(["-Capture", "-Json"], writer);
        Assert.Equal(1, exit);
        Assert.Contains("-Game", writer.ToString());
    }

    [Fact]
    public void Capture_rejects_game_and_process_name_together()
    {
        using var writer = new StringWriter();
        var exit = CliHost.Run(["-Capture", "-Game", @"C:\fake\game.exe", "-ProcessName", "game", "-Json"], writer);
        Assert.Equal(1, exit);
        Assert.Contains("二选一", writer.ToString());
    }

    [Fact]
    public void Capture_rejects_apply_only_options()
    {
        using var writer = new StringWriter();
        var exit = CliHost.Run(["-Capture", "-ProcessName", "dwm", "-Items", "a,b", "-Json"], writer);
        Assert.Equal(1, exit);
        Assert.Contains("只接受", writer.ToString());
    }
}
