using System.IO;
using System.Windows.Threading;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 性能会话服务：应用级拥有，页面切换不影响运行中的会话。
/// 运行中每 AutosaveEverySamples 个样本快照一次到 _active.json，
/// 进程异常退出后由 PerformanceSessionStore.RecoverInterruptedSession 在下次启动恢复。
/// </summary>
public sealed class PerformanceSessionService : IDisposable
{
    /// <summary>内存缓冲上限：正常 14400（≥4 小时 @1s）；低配 4800（采样 3 秒，等效覆盖更久且内存更省）。</summary>
    public static int CurrentBufferCapacity => UiPerformance.LowSpec ? 4800 : 14400;
    public const int AutosaveEverySamples = 60;

    private MetricsSampler? _sampler;
    private string _name = "";
    private DateTime _startedAt;
    private int _sampleCount;
    private int _disposed;

    public bool IsRunning { get; private set; }
    public string SessionName => _name;
    public DateTime StartedAt => _startedAt;
    public MetricSample? LatestSample { get; private set; }
    public TimeSpan Elapsed => IsRunning ? DateTime.Now - _startedAt : TimeSpan.Zero;

    /// <summary>当前采样间隔（低配模式 3 秒，正常 1 秒）。</summary>
    public static TimeSpan CurrentInterval => TimeSpan.FromSeconds(UiPerformance.LowSpec ? 3 : 1);

    /// <summary>新样本（在创建线程回调）。</summary>
    public event Action<MetricSample>? Sampled;
    /// <summary>开始/结束等状态变化。</summary>
    public event Action? StateChanged;

    /// <summary>运行中最近一次各指标不可用原因（空字典 = 全部可用）。</summary>
    public IReadOnlyDictionary<string, string> CurrentUnavailableReasons
        => _sampler?.UnavailableReasons ?? new Dictionary<string, string>();

    /// <summary>运行中已缓冲的样本（只读快照语义，UI 线程使用）。</summary>
    public IReadOnlyList<MetricSample> RunningBuffer => _sampler?.Buffer ?? Array.Empty<MetricSample>();

    public void Start(string name)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PerformanceSessionService));
        if (IsRunning)
            return;
        _name = string.IsNullOrWhiteSpace(name) ? DefaultSessionName() : name.Trim();
        _startedAt = DateTime.Now;
        _sampleCount = 0;
        _sampler = new MetricsSampler(CurrentInterval, CurrentBufferCapacity);
        _sampler.Sampled += OnSample;
        IsRunning = true;
        _sampler.SampleOnce(); // 立即出第一个样本，界面无需等一个间隔
        _sampler.Start();
        StateChanged?.Invoke();
    }

    /// <summary>结束会话；save=false 等价取消（丢弃数据）。返回保存或构造出的会话，未保存时返回 null。</summary>
    public PerformanceSession? Stop(bool save = true)
    {
        if (!IsRunning)
            return null;
        IsRunning = false;
        if (_sampler is not null)
        {
            _sampler.Sampled -= OnSample;
            var endedAt = DateTime.Now;
            var session = ToSession(endedAt);
            _sampler.Dispose();
            _sampler = null;
            LatestSample = null;
            if (save)
            {
                PerformanceSessionStore.Save(session);
                PerformanceSessionStore.EnforceRetention();
                PerformanceSessionStore.ClearActiveSnapshot();
            }
            else
            {
                PerformanceSessionStore.ClearActiveSnapshot();
            }
            StateChanged?.Invoke();
            return save ? session : null;
        }
        StateChanged?.Invoke();
        return null;
    }

    public void Cancel() => Stop(save: false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (IsRunning)
            Stop(save: false);
        _sampler?.Dispose();
        _sampler = null;
    }

    private PerformanceSession ToSession(DateTime endedAt)
    {
        var buffer = _sampler?.Buffer ?? Array.Empty<MetricSample>();
        var points = new List<SessionSamplePoint>(buffer.Count);
        foreach (var s in buffer)
            points.Add(new SessionSamplePoint(
                s.Timestamp,
                s.CpuPercent,
                s.MemoryPercent,
                s.GpuPercent,
                s.VramUsedBytes is { } b && double.IsFinite(b) ? Math.Round(b / 1024.0 / 1024.0, 1) : null));
        return new PerformanceSession(
            PerformanceSessionStore.NewSessionId(),
            _name,
            _startedAt,
            endedAt,
            PerformanceSessionStore.CurrentSchemaVersion,
            CurrentInterval.TotalSeconds,
            points);
    }

    private void OnSample(MetricSample sample)
    {
        LatestSample = sample;
        _sampleCount++;
        if (_sampleCount % AutosaveEverySamples == 0 && _sampler is not null)
        {
            var snapshot = ToSession(DateTime.Now) with { Id = "active-snapshot" };
            PerformanceSessionStore.SaveActiveSnapshot(snapshot);
        }
        Sampled?.Invoke(sample);
    }

    public static string DefaultSessionName() => "会话 " + DateTime.Now.ToString("MM-dd HH:mm");

    public static bool IsValidSessionName(string? name)
    {
        var t = (name ?? "").Trim();
        return t.Length > 0
            && t.Length <= 60
            && t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !t.Any(char.IsControl);
    }
}
