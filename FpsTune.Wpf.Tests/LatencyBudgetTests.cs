using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 延迟预算（0.5.0 M5）：PresentMon v1/v2 多列解析、缺列如实为空、三段数值与来源标注、建议键触发。
/// 列名表来自官方文档：v2 默认列 FrameTime/CPUBusy/GPUBusy/DisplayLatency；v1 列 msBetweenPresents/
/// msInPresentAPI/msGPUActive/msUntilDisplayed。
/// </summary>
public sealed class FrameTimeColumnsTests
{
    private const string HeaderV2 =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,ClickToPhotonLatency";

    private const string HeaderV1 =
        "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInSeconds,msInPresentAPI,msUntilRenderComplete,msUntilDisplayed,Dropped,msBetweenPresents,msBetweenDisplayChange,msUntilRenderStart,msGPUActive,msGPUVideoActive";

    private static string RowV2(double frame, double gpu, double display) =>
        $"game.exe,123,0x1,DXGI,0,0,0,Composed: Flip,10,{frame:0.000},2.5,3.0,1.0,14.0,{gpu:0.000},1.0,{display:0.000},16.5,0.4,25.0";

    private static string RowV1(double frame, double gpu, double display) =>
        $"game.exe,123,0x1,D3D9,0,0,0,Hardware: Independent Flip,10.0,1.2,14.0,{display:0.000},0,{frame:0.000},16.5,2.0,{gpu:0.000},0.0";

    [Fact]
    public void Reads_v2_default_columns()
    {
        var lines = new List<string> { HeaderV2, RowV2(16.667, 12.0, 5.0), RowV2(16.9, 13.0, 6.0) };
        var series = FrameTimeColumns.Read(lines);

        Assert.NotNull(series);
        Assert.Equal([16.667, 16.9], series!.FrameTimesMs);
        Assert.Equal([12.0, 13.0], series.GpuBusyMs);
        Assert.Equal([2.5, 2.5], series.CpuBusyMs);
        Assert.Equal([5.0, 6.0], series.DisplayLatencyMs);
    }

    [Fact]
    public void Reads_v1_columns()
    {
        var lines = new List<string> { HeaderV1, RowV1(16.667, 12.5, 5.5) };
        var series = FrameTimeColumns.Read(lines);

        Assert.NotNull(series);
        Assert.Equal([16.667], series!.FrameTimesMs);
        Assert.Equal([12.5], series.GpuBusyMs);
        Assert.Equal([1.2], series.CpuBusyMs);
        Assert.Equal([5.5], series.DisplayLatencyMs);
    }

    [Fact]
    public void Missing_optional_columns_are_empty_not_error()
    {
        // 只有帧时间列的 CSV（会话采样器的单列输出）。
        var lines = new List<string> { "FrameTime", "16.667", "16.7" };
        var series = FrameTimeColumns.Read(lines);

        Assert.NotNull(series);
        Assert.Equal(2, series!.FrameTimesMs.Count);
        Assert.Empty(series.GpuBusyMs);
        Assert.Empty(series.CpuBusyMs);
        Assert.Empty(series.DisplayLatencyMs);
    }

    [Fact]
    public void Unknown_header_returns_null()
    {
        Assert.Null(FrameTimeColumns.Read(new[] { "foo,bar", "1,2" }));
        Assert.Null(FrameTimeColumns.Read(Array.Empty<string>()));
    }

    [Fact]
    public void Quoted_application_field_does_not_shift_columns()
    {
        var lines = new List<string>
        {
            HeaderV2,
            "\"app, with comma\",123,0x1,DXGI,0,0,0,Composed: Flip,10,16.667,2.5,3.0,1.0,14.0,12.0,1.0,5.0,16.5,0.4,25.0",
        };
        var series = FrameTimeColumns.Read(lines);

        Assert.Equal([16.667], series!.FrameTimesMs);
        Assert.Equal([12.0], series.GpuBusyMs);
    }
}

/// <summary>三段预算的数值口径、来源标注与建议键。</summary>
public sealed class LatencyBudgetTests
{
    private static FrameColumnSeries Series(int frames = 100, double frameMs = 16.0, double gpuMs = 10.0, double displayMs = 4.0) =>
        new(
            Enumerable.Range(0, frames).Select(i => frameMs + (i % 3) * 0.4).ToList(),
            Enumerable.Range(0, frames).Select(i => gpuMs).ToList(),
            [], // CPU 列缺失：对应项不可测
            Enumerable.Range(0, frames).Select(i => displayMs + (i % 2)).ToList());

    [Fact]
    public void Evaluate_marks_all_segments_measured_when_columns_present()
    {
        var budget = LatencyBudget.Evaluate(Series(), 1000, 128, 240);

        Assert.Equal("derived", budget.InputInterval.Source);
        Assert.Equal(1.0, budget.InputInterval.ValueMs!.Value, 3);
        Assert.Equal(128.0, budget.InputQueue.ValueMs!.Value, 1);
        Assert.Equal("measured", budget.FrameP50.Source);
        Assert.Equal("measured", budget.FrameP99.Source);
        Assert.Equal("measured", budget.GpuBusyP50.Source);
        Assert.Equal("measured", budget.DisplayLatencyP50.Source);
        Assert.True(budget.RefreshMeasured);
        Assert.Equal(240, budget.RefreshHz);
    }

    [Fact]
    public void Percentiles_follow_nearest_rank()
    {
        // 100 帧：p50 = 第 50 个，p99 = 第 99 个（排序后）。
        var frames = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        var series = new FrameColumnSeries(frames, [], [], []);
        var budget = LatencyBudget.Evaluate(series, 1000, 128, null);

        Assert.Equal(50, budget.FrameP50.ValueMs);
        Assert.Equal(99, budget.FrameP99.ValueMs);
    }

    [Fact]
    public void Missing_columns_are_unavailable_not_zero()
    {
        var budget = LatencyBudget.Evaluate(new FrameColumnSeries([16.0], [], [], []), 1000, 128, null);

        Assert.Equal("unavailable", budget.GpuBusyP50.Source);
        Assert.Null(budget.GpuBusyP50.ValueMs);
        Assert.Equal("unavailable", budget.DisplayLatencyP50.Source);
        Assert.False(budget.RefreshMeasured);
        Assert.Null(budget.RefreshHz);
    }

    [Fact]
    public void Null_series_keeps_input_segment_derived()
    {
        var budget = LatencyBudget.Evaluate(null, 1000, 128, 144);

        Assert.Equal(1.0, budget.InputInterval.ValueMs!.Value, 3);
        Assert.Equal("unavailable", budget.FrameP50.Source);
        Assert.True(budget.RefreshMeasured);
    }

    [Fact]
    public void Low_polling_rate_and_shallow_queue_trigger_advice()
    {
        var budget = LatencyBudget.Evaluate(Series(), 500, 16, 240);

        Assert.Contains("Str.BudgetAdvicePollingRate", budget.AdviceKeys);
        Assert.Contains("Str.BudgetAdviceQueueDepth", budget.AdviceKeys);
    }

    [Fact]
    public void Gpu_bound_frame_times_trigger_advice()
    {
        // GPU 忙 15ms / 帧时间 16ms = 93.75% ≥ 85%。
        var budget = LatencyBudget.Evaluate(Series(frameMs: 16.0, gpuMs: 15.0), 1000, 128, 240);

        Assert.Contains("Str.BudgetAdviceGpuBound", budget.AdviceKeys);
    }

    [Fact]
    public void High_display_latency_variance_triggers_advice()
    {
        // 90 帧 4ms + 10 帧 12ms：p99（nearest-rank）落在 12ms ≥ 2× p50(4ms)。
        var series = new FrameColumnSeries(
            Enumerable.Repeat(16.0, 100).ToList(),
            [], [],
            Enumerable.Range(0, 100).Select(i => i < 90 ? 4.0 : 12.0).ToList());
        var budget = LatencyBudget.Evaluate(series, 1000, 128, 240);

        Assert.Contains("Str.BudgetAdviceDisplayQueue", budget.AdviceKeys);
    }

    [Fact]
    public void Adequate_input_triggers_no_input_advice()
    {
        var budget = LatencyBudget.Evaluate(Series(), 1000, 128, 240);

        Assert.DoesNotContain("Str.BudgetAdviceQueueDepth", budget.AdviceKeys);
        Assert.DoesNotContain("Str.BudgetAdvicePollingRate", budget.AdviceKeys);
        Assert.DoesNotContain("Str.BudgetAdviceGpuBound", budget.AdviceKeys);
    }
}
