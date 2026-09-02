using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>采样内核：GPU 计数器实例解析、去重聚合、采样器生命周期与有界缓冲。</summary>
public class MetricsKernelTests
{
    // ---------- GPU Engine 实例名解析 ----------

    [Fact]
    public void ParseEngineInstance_reads_pid_luid_phys_engtype()
    {
        var inst = GpuCounterMath.ParseEngineInstance(
            "pid_1234_luid_0x00000000_0x0000C770_phys_0_eng_0_engtype_3D");
        Assert.Equal("1234", inst.Pid);
        Assert.Equal("0x00000000_0x0000C770_phys_0", inst.Adapter);
        Assert.Equal("3D", inst.EngineType);
    }

    [Fact]
    public void ParseEngineInstance_handles_adapter_memory_names_and_garbage()
    {
        var adapter = GpuCounterMath.ParseEngineInstance("luid_0x00000000_0x0000ABCD_phys_1");
        Assert.Null(adapter.Pid);
        Assert.Equal("0x00000000_0x0000ABCD_phys_1", adapter.Adapter);

        var garbage = GpuCounterMath.ParseEngineInstance("weird instance");
        Assert.Equal("weird instance", garbage.Adapter);
        Assert.Equal("unknown", garbage.EngineType);
    }

    [Fact]
    public void ParseEngineInstance_is_case_insensitive_for_counter_tokens()
    {
        var inst = GpuCounterMath.ParseEngineInstance(
            "PID_1234_LUID_0x00000000_0x0000C770_PHYS_0_ENG_0_ENGTYPE_3D");
        Assert.Equal("1234", inst.Pid);
        Assert.Equal("0x00000000_0x0000C770_phys_0", inst.Adapter);
        Assert.Equal("3D", inst.EngineType);
    }

    // ---------- 利用率聚合：多引擎去重、多进程求和、多适配器取最大 ----------

    [Fact]
    public void AggregateGpuUtilization_sums_processes_within_engine_and_takes_max_engine()
    {
        // 同一适配器：3D 引擎两个进程 30+40=70；Copy 引擎 20 → 适配器 70
        var samples = new (string, double)[]
        {
            ("pid_1_luid_0xA_0xB_phys_0_eng_0_engtype_3D", 30),
            ("pid_2_luid_0xA_0xB_phys_0_eng_0_engtype_3D", 40),
            ("pid_1_luid_0xA_0xB_phys_0_eng_1_engtype_Copy", 20)
        };
        Assert.Equal(70, GpuCounterMath.AggregateGpuUtilization(samples));
    }

    [Fact]
    public void AggregateGpuUtilization_multi_adapter_takes_max_not_sum()
    {
        var samples = new (string, double)[]
        {
            ("pid_1_luid_0xA_0xB_phys_0_eng_0_engtype_3D", 80),
            ("pid_1_luid_0xC_0xD_phys_0_eng_0_engtype_3D", 50)
        };
        Assert.Equal(80, GpuCounterMath.AggregateGpuUtilization(samples));
    }

    [Fact]
    public void AggregateGpuUtilization_clamps_and_skips_invalid()
    {
        var samples = new (string, double)[]
        {
            ("pid_1_luid_0xA_0xB_phys_0_eng_0_engtype_3D", 90),
            ("pid_2_luid_0xA_0xB_phys_0_eng_0_engtype_3D", 90) // 同引擎多进程求和 180 → 钳到 100
        };
        Assert.Equal(100, GpuCounterMath.AggregateGpuUtilization(samples));
        Assert.Null(GpuCounterMath.AggregateGpuUtilization(Array.Empty<(string, double)>()));
        Assert.Null(GpuCounterMath.AggregateGpuUtilization(new (string, double)[] { ("pid_1_x", double.NaN) }));
    }

    [Fact]
    public void AggregateGpuUtilization_skips_unknown_counter_identity()
    {
        Assert.Null(GpuCounterMath.AggregateGpuUtilization(
            new (string, double)[] { ("not-a-gpu-instance", 100) }));
        Assert.Null(GpuCounterMath.AggregateGpuUtilization(
            new (string, double)[] { ("pid_1_luid_0x0_0x1_phys_0_engtype_", 100) }));
    }

    // ---------- 显存聚合：按适配器去重 ----------

    [Fact]
    public void AggregateAdapterDedicatedBytes_dedups_same_adapter_and_sums_across_adapters()
    {
        var samples = new (string, double)[]
        {
            ("luid_0xA_0xB_phys_0", 2.0 * 1024 * 1024 * 1024),
            ("luid_0xA_0xB_phys_0", 1.5 * 1024 * 1024 * 1024), // 同适配器重复实例：取最大
            ("luid_0xC_0xD_phys_0", 0.5 * 1024 * 1024 * 1024)
        };
        Assert.Equal(2.5 * 1024 * 1024 * 1024, GpuCounterMath.AggregateAdapterDedicatedBytes(samples));
    }

    [Fact]
    public void AggregateAdapterDedicatedBytes_ignores_negative_and_empty()
    {
        Assert.Null(GpuCounterMath.AggregateAdapterDedicatedBytes(new (string, double)[] { ("x", -5) }));
        Assert.Equal(100, GpuCounterMath.AggregateAdapterDedicatedBytes(
            new (string, double)[] { ("luid_0xA_0xB_phys_0", 100) }));
    }

    [Fact]
    public void FormatBytesMiB_handles_null()
    {
        Assert.Equal("不可用", GpuCounterMath.FormatBytesMiB(null));
        Assert.Equal("不可用", GpuCounterMath.FormatBytesMiB(-1));
        Assert.Equal("1024 MiB", GpuCounterMath.FormatBytesMiB(1024.0 * 1024 * 1024));
    }

    // ---------- 采样器生命周期（真实计数器，失败时返回不可用而不是伪造 0） ----------

    [Fact]
    public void Sampler_SampleOnce_returns_sample_and_records_unavailable_reasons()
    {
        using var sampler = new MetricsSampler(TimeSpan.FromSeconds(1), capacity: 10);
        var sample = sampler.SampleOnce();
        Assert.True(sample.CpuPercent is null || (sample.CpuPercent >= 0 && sample.CpuPercent <= 100));
        // 不可用的指标必须有原因说明，不允许无声缺失
        if (sample.GpuPercent is null)
            Assert.True(sampler.UnavailableReasons.ContainsKey("gpu"), "GPU 不可用时必须给出原因");
        if (sample.VramUsedBytes is null)
            Assert.True(sampler.UnavailableReasons.ContainsKey("vram"), "显存不可用时必须给出原因");
        Assert.Single(sampler.Buffer);
    }

    [Fact]
    public void Sampler_buffer_is_bounded()
    {
        using var sampler = new MetricsSampler(TimeSpan.FromSeconds(1), capacity: 5);
        for (var i = 0; i < 12; i++)
            sampler.SampleOnce();
        Assert.Equal(5, sampler.Buffer.Count);
    }

    [Fact]
    public void Sampler_start_stop_dispose_is_idempotent()
    {
        var sampler = new MetricsSampler(TimeSpan.FromSeconds(1), capacity: 10);
        sampler.Start();
        Assert.True(sampler.IsRunning);
        sampler.Start(); // 重复 Start 无副作用
        Assert.True(sampler.IsRunning);
        sampler.Stop();
        Assert.False(sampler.IsRunning);
        sampler.Dispose();
        sampler.Dispose(); // 双重 Dispose 安全
        Assert.False(sampler.IsRunning);
        Assert.Empty(sampler.Buffer);
    }

    [Fact]
    public void Sampler_Sampled_event_fires_and_survives_subscriber_exception()
    {
        var count = 0;
        using var sampler = new MetricsSampler(TimeSpan.FromSeconds(1), capacity: 10);
        sampler.Sampled += _ => count++;
        sampler.Sampled += _ => throw new InvalidOperationException("订阅方异常");
        sampler.SampleOnce();
        Assert.Equal(1, count); // 第一个订阅者仍被通知，采样不中断
    }

    [Fact]
    public void Sampler_throws_ObjectDisposed_after_dispose()
    {
        var sampler = new MetricsSampler();
        sampler.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sampler.SampleOnce());
    }
}
