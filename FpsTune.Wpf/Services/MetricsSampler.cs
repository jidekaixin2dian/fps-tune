using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FpsTune.Wpf.Services;

/// <summary>一次指标采样。null 表示该指标本轮不可用（原因见 UnavailableReasons）。</summary>
public sealed record MetricSample(
    DateTime Timestamp,
    double? CpuPercent,
    double? MemoryPercent,
    double? GpuPercent,
    double? VramUsedBytes,
    double? VramTotalBytes);

/// <summary>
/// 可释放、实例拥有的本地性能采样内核：CPU / 内存 / GPU 利用率 / GPU 专用显存。
/// 全部只读性能数据（性能计数器 + GlobalMemoryStatusEx + 显示类注册表键），
/// 不需要管理员权限，不触碰任何游戏进程，不读进程路径或进程内存。
///
/// 生命周期归调用方所有：Start/Stop 可重复，Dispose 释放全部计数器与计时器；
/// 缓冲区有界（capacity），不会无限增长。
/// </summary>
public sealed class MetricsSampler : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly int _capacity;
    private readonly DispatcherTimer? _timer;
    private readonly List<MetricSample> _buffer = new();
    private readonly Dictionary<string, string> _unavailable = new();
    private IReadOnlyDictionary<string, string> _publishedReasons = new Dictionary<string, string>();
    private readonly object _counterGate = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<MetricSample>? _readOverride;
    private int _sampling;
    private int _generation;
    private double? _publishedVramTotal;

    private PerformanceCounter? _cpu;
    private bool _cpuPrimed;
    private List<PerformanceCounter>? _gpuEngineCounters;
    private List<PerformanceCounter>? _vramCounters;
    private int _gpuRefreshCountdown;
    private int _vramRefreshCountdown;
    private double? _vramTotalBytes;
    private bool _vramTotalQueried;
    private int _disposed;

    /// <summary>新样本产生时触发（DispatcherTimer 保证在创建线程上回调）。</summary>
    public event Action<MetricSample>? Sampled;

    public MetricsSampler(TimeSpan? interval = null, int capacity = 600)
        : this(interval, capacity, null) { }

    internal MetricsSampler(TimeSpan? interval, int capacity, Func<MetricSample>? readOverride)
    {
        _readOverride = readOverride;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _capacity = Math.Max(1, capacity);
        // 计时器随实例创建：由 Start/Stop 控制启停，Dispose 兜底停止。
        _timer = new DispatcherTimer { Interval = _interval };
        _timer.Tick += async (_, _) => await SampleOnceAsync();
    }

    /// <summary>采样间隔。</summary>
    public TimeSpan Interval => _interval;

    /// <summary>是否正在采样。</summary>
    public bool IsRunning => _timer?.IsEnabled == true;

    /// <summary>有界历史缓冲（最多 capacity 个样本，FIFO 淘汰）。</summary>
    public IReadOnlyList<MetricSample> Buffer => _buffer;

    /// <summary>各指标最近一次不可用的原因（键：cpu/mem/gpu/vram/vram-total）。</summary>
    public IReadOnlyDictionary<string, string> UnavailableReasons => _publishedReasons;

    /// <summary>GPU 专用显存容量（字节）；0 = 未能可靠取得。</summary>
    public double? VramTotalBytes => _publishedVramTotal;

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (IsRunning) return;
        _timer?.Start();
        _ = SampleOnceAsync();
    }

    public void Stop()
    {
        _timer?.Stop();
        Interlocked.Increment(ref _generation);
    }

    /// <summary>立即采样一次并写入缓冲、触发 Sampled。</summary>
    public MetricSample SampleOnce()
    {
        lock (_counterGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var sample = ReadSample();
            Publish(sample, new Dictionary<string, string>(_unavailable));
            return sample;
        }
    }

    /// <summary>后台读取计数器；最多一轮在途，停止/释放后丢弃迟到结果。</summary>
    public async Task SampleOnceAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
            return;
        var generation = Volatile.Read(ref _generation);
        try
        {
            var result = await Task.Run(() =>
            {
                lock (_counterGate)
                {
                    if (Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _generation))
                        return ((MetricSample?)null, new Dictionary<string, string>());
                    return ((MetricSample?)ReadSample(), new Dictionary<string, string>(_unavailable));
                }
            }).ConfigureAwait(false);
            if (result.Item1 is null || _dispatcher.HasShutdownStarted) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) == 0 && generation == Volatile.Read(ref _generation))
                    Publish(result.Item1, result.Item2);
            });
        }
        catch (OperationCanceledException) when (_dispatcher.HasShutdownStarted) { }
        finally { Interlocked.Exchange(ref _sampling, 0); }
    }

    private MetricSample ReadSample()
    {
        if (_readOverride is not null) return _readOverride();
        var now = DateTime.Now;
        var vramUsed = ReadVramUsedBytes();

        return new MetricSample(
            now,
            ReadCpuPercent(),
            ReadMemoryPercent(),
            ReadGpuPercent(),
            vramUsed,
            ReadVramTotalBytes());

    }

    private void Publish(MetricSample sample, IReadOnlyDictionary<string, string> reasons)
    {
        _publishedReasons = reasons;
        _publishedVramTotal = sample.VramTotalBytes;
        _buffer.Add(sample);
        while (_buffer.Count > _capacity)
            _buffer.RemoveAt(0);

        try
        {
            Sampled?.Invoke(sample);
        }
        catch
        {
            // 订阅方异常不应中断采样循环，也不应把 listener 伪装成缺失指标。
        }
    }

    public void ClearBuffer() => _buffer.Clear();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        Sampled = null;
        _buffer.Clear();
        _publishedReasons = new Dictionary<string, string>();
        // 不能在 UI 线程等待一次慢的驱动查询结束；计数器只在后台锁内释放。
        _ = Task.Run(() =>
        {
            lock (_counterGate)
            {
                _cpu?.Dispose();
                _cpu = null;
                DisposeList(_gpuEngineCounters);
                _gpuEngineCounters = null;
                DisposeList(_vramCounters);
                _vramCounters = null;
                _unavailable.Clear();
            }
        });
    }

    // ---------- 各指标读取 ----------

    private double? ReadCpuPercent()
    {
        try
        {
            _cpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            var v = _cpu.NextValue();
            if (!_cpuPrimed)
            {
                // 首次 NextValue 恒为 0，仅作基线
                _cpuPrimed = true;
                return SetReason("cpu", "首次采样仅作基线");
            }
            _unavailable.Remove("cpu");
            return Math.Clamp(v, 0, 100);
        }
        catch (Exception ex)
        {
            return SetReason("cpu", "CPU 计数器不可用：" + ex.Message);
        }
    }

    private double? ReadMemoryPercent()
    {
        try
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref st))
                return SetReason("mem", "内存状态查询失败");
            _unavailable.Remove("mem");
            return st.dwMemoryLoad;
        }
        catch (Exception ex)
        {
            return SetReason("mem", "内存状态不可用：" + ex.Message);
        }
    }

    private double? ReadGpuPercent()
    {
        try
        {
            var refreshed = false;
            // 实例（随游戏进程增减）定期重建；先构建新列表再释放旧的，
            // 构建中途抛异常（驱动过旧等）时旧计数器仍可用。
            if (_gpuEngineCounters is null || _gpuRefreshCountdown-- <= 0)
            {
                _gpuRefreshCountdown = 20;
                var fresh = new List<PerformanceCounter>();
                var committed = false;
                try
                {
                    var category = new PerformanceCounterCategory("GPU Engine");
                    foreach (var name in category.GetInstanceNames())
                        fresh.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true));
                    DisposeList(_gpuEngineCounters);
                    _gpuEngineCounters = fresh;
                    committed = true;
                    refreshed = true;
                }
                finally
                {
                    // A constructor can fail after earlier instances were opened.
                    // Dispose the uncommitted list so a refresh cannot leak handles.
                    if (!committed)
                        DisposeList(fresh);
                }
            }
            if (_gpuEngineCounters.Count == 0)
            {
                // 空实例列表可能只是驱动/适配器尚未就绪，下一轮立即重试。
                _gpuRefreshCountdown = 0;
                return SetReason("gpu", "系统未提供 GPU Engine 性能计数器（驱动过旧或虚拟机）");
            }

            if (refreshed)
            {
                // PerformanceCounter 的首个 NextValue 仅建立基线。不能在同一
                // 采样中立刻再读，否则会把尚未计算出的值误报为 0%。
                var primed = 0;
                foreach (var c in _gpuEngineCounters)
                {
                    try { c.NextValue(); primed++; } catch { /* 实例可能刚消失 */ }
                }
                if (primed == 0)
                {
                    _gpuRefreshCountdown = 0;
                    return SetReason("gpu", "GPU 计数器无法建立采样基线");
                }
                return SetReason("gpu", "GPU 计数器刚刷新，等待下一采样间隔");
            }

            var values = new List<(string Instance, double Value)>(_gpuEngineCounters.Count);
            foreach (var c in _gpuEngineCounters)
            {
                try
                {
                    values.Add((c.InstanceName, c.NextValue()));
                }
                catch
                {
                    // 单个实例消失（进程退出）时跳过，下一轮刷新重建
                }
            }
            var aggregated = GpuCounterMath.AggregateGpuUtilization(values);
            if (aggregated is null)
            {
                _gpuRefreshCountdown = 0;
                return SetReason("gpu", "GPU Engine 计数器存在但无有效样本");
            }
            _unavailable.Remove("gpu");
            return aggregated;
        }
        catch (Exception ex)
        {
            return SetReason("gpu", "GPU 计数器不可用：" + ex.Message);
        }
    }

    private double? ReadVramUsedBytes()
    {
        try
        {
            var refreshed = false;
            // 显存适配器实例也会随适配器休眠/唤醒和热插拔变化，不能只在
            // 第一次读取时缓存；定期重建与 GPU Engine 使用相同的有界周期。
            if (_vramCounters is null || _vramRefreshCountdown-- <= 0)
            {
                _vramRefreshCountdown = 20;
                var fresh = new List<PerformanceCounter>();
                var committed = false;
                try
                {
                    var category = new PerformanceCounterCategory("GPU Adapter Memory");
                    foreach (var name in category.GetInstanceNames())
                        fresh.Add(new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", name, readOnly: true));
                    DisposeList(_vramCounters);
                    _vramCounters = fresh;
                    committed = true;
                    refreshed = true;
                }
                finally
                {
                    // A constructor can fail after earlier instances were opened.
                    if (!committed)
                        DisposeList(fresh);
                }
            }
            if (_vramCounters.Count == 0)
            {
                _vramRefreshCountdown = 0;
                return SetReason("vram", "系统未提供 GPU Adapter Memory 计数器，无法读取专用显存用量");
            }

            if (refreshed)
            {
                // 和 GPU Engine 一样，首个读数只是基线，避免把基线 0 当成
                // 真实的显存用量写入会话。
                var primed = 0;
                foreach (var c in _vramCounters)
                {
                    try { c.NextValue(); primed++; } catch { /* 实例可能刚消失 */ }
                }
                if (primed == 0)
                {
                    _vramRefreshCountdown = 0;
                    return SetReason("vram", "显存计数器无法建立采样基线");
                }
                return SetReason("vram", "显存计数器刚刷新，等待下一采样间隔");
            }

            var values = new List<(string Instance, double Value)>(_vramCounters.Count);
            foreach (var c in _vramCounters)
            {
                try
                {
                    values.Add((c.InstanceName, c.NextValue()));
                }
                catch
                {
                    // 实例消失（适配器休眠等）时跳过
                }
            }
            var total = GpuCounterMath.AggregateAdapterDedicatedBytes(values);
            if (total is null)
            {
                _vramRefreshCountdown = 0;
                return SetReason("vram", "显存计数器存在但无有效样本");
            }
            _unavailable.Remove("vram");
            return total;
        }
        catch (Exception ex)
        {
            // Drop and dispose the current list before forcing a rebuild. This
            // also covers a refresh that failed after the old list was created;
            // simply nulling the field here would leak its native counter handles.
            var stale = _vramCounters;
            _vramCounters = null;
            DisposeList(stale);
            _vramRefreshCountdown = 0;
            return SetReason("vram", "显存计数器不可用：" + ex.Message);
        }
    }

    private double? ReadVramTotalBytes()
    {
        if (_vramTotalQueried)
            return _vramTotalBytes > 0 ? _vramTotalBytes : null;
        _vramTotalQueried = true;
        try
        {
            _vramTotalBytes = VramCapacityQuery.ReadTotalBytes();
            if (_vramTotalBytes <= 0)
                SetReason("vram-total", "无法从注册表读取显卡显存容量");
            else
                _unavailable.Remove("vram-total");
        }
        catch (Exception ex)
        {
            _vramTotalBytes = 0;
            SetReason("vram-total", "显存容量读取失败：" + ex.Message);
        }
        return _vramTotalBytes > 0 ? _vramTotalBytes : null;
    }

    private double? SetReason(string key, string reason)
    {
        _unavailable[key] = reason;
        return null;
    }

    private static void DisposeList(List<PerformanceCounter>? counters)
    {
        if (counters is null)
            return;
        foreach (var c in counters)
        {
            try { c.Dispose(); } catch { /* 释放失败不致命 */ }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

/// <summary>
/// 显存容量查询：注册表显示类键的 HardwareInformation.qwMemorySize，
/// 按 MatchingDeviceId 去重后求和。只读，取不到时返回 0。
/// </summary>
public static class VramCapacityQuery
{
    public static long ReadTotalBytes()
    {
        const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(classPath);
        if (key is null)
            return 0;
        foreach (var sub in key.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.StartsWith("00", StringComparison.Ordinal))
                continue;
            using var sk = key.OpenSubKey(sub);
            if (sk is null)
                continue;
            var matchId = sk.GetValue("MatchingDeviceId")?.ToString() ?? sub;
            if (!seen.Add(matchId))
                continue;
            var mem = sk.GetValue("HardwareInformation.qwMemorySize");
            long bytes = mem switch
            {
                long l when l > 0 => l,
                int i when i > 0 => i,
                _ => 0
            };
            total += bytes;
        }
        return total;
    }
}
