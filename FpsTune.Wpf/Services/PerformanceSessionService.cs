using System.IO;
using System.Runtime.ExceptionServices;
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
    private IDisposable? _activeOwner;

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

    public bool Start(string name)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PerformanceSessionService));
        if (IsRunning)
            return false;

        var owner = PerformanceSessionStore.TryAcquireActiveSessionOwnership();
        if (owner is null)
            return false;
        _activeOwner = owner;
        try
        {
            // A leftover active snapshot without a cancellation tombstone is
            // recoverable data and must never be overwritten by a new session.
            if (!PerformanceSessionStore.PrepareForNewSession())
            {
                ReleaseActiveOwner();
                return false;
            }

            _name = string.IsNullOrWhiteSpace(name) ? DefaultSessionName() : name.Trim();
            _startedAt = DateTime.Now;
            _sampleCount = 0;
            _sampler = new MetricsSampler(CurrentInterval, CurrentBufferCapacity);
            _sampler.Sampled += OnSample;
            IsRunning = true;
            _sampler.SampleOnce(); // 立即出第一个样本，界面无需等一个间隔
            _sampler.Start();
            StateChanged?.Invoke();
            return true;
        }
        catch
        {
            IsRunning = false;
            _sampler?.Dispose();
            _sampler = null;
            LatestSample = null;
            ReleaseActiveOwner();
            throw;
        }
    }

    /// <summary>结束会话；save=false 等价取消（丢弃数据）。返回保存或构造出的会话，未保存时返回 null。</summary>
    public PerformanceSession? Stop(bool save = true)
    {
        if (!IsRunning)
            return null;

        var sampler = _sampler;
        PerformanceSession? session = null;
        Exception? failure = null;
        try
        {
            // Stop the timer before taking the final snapshot. This keeps the
            // sampler from ticking while persistence is in progress.
            sampler?.Stop();
            if (sampler is not null)
            {
                sampler.Sampled -= OnSample;
                session = ToSession(DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // Cleanup is unconditional: a full disk or a malformed callback must
            // never leave a live DispatcherTimer/event subscription behind.
            IsRunning = false;
            sampler?.Dispose();
            _sampler = null;
            LatestSample = null;
        }

        if (save && session is not null && failure is null)
        {
            var sessionPersisted = false;
            try
            {
                PerformanceSessionStore.Save(session);
                sessionPersisted = true;
                PerformanceSessionStore.EnforceRetention();
                if (PerformanceSessionStore.HasActiveSnapshot)
                {
                    // The historical save is complete, so persist a durable
                    // "handled" tombstone before attempting active cleanup.
                    if (!PerformanceSessionStore.MarkActiveSnapshotHandledOwned())
                        throw new IOException("无法写入会话已处理标记；active 快照仍保留。");
                    if (PerformanceSessionStore.ClearActiveSnapshotOwned())
                        PerformanceSessionStore.ClearActiveCancellationOwned();
                }
                else
                {
                    PerformanceSessionStore.ClearActiveCancellationOwned();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                if (!sessionPersisted)
                {
                    // Keep a complete fallback snapshot for the next launch when
                    // the final historical write fails (for example, disk full).
                    PerformanceSessionStore.SaveActiveSnapshotOwned(session with { Id = "active-snapshot" });
                }
            }
        }
        else if (!save)
        {
            // Write the tombstone before deleting. If deletion fails, the next
            // process will honor the cancellation and only retry cleanup.
            if (PerformanceSessionStore.HasActiveSnapshot &&
                !PerformanceSessionStore.MarkActiveSnapshotCancelledOwned())
            {
                failure ??= new IOException("无法写入取消标记；会话未取消，active 快照仍保留。");
            }
            else if (PerformanceSessionStore.HasActiveSnapshot &&
                     PerformanceSessionStore.ClearActiveSnapshotOwned())
            {
                PerformanceSessionStore.ClearActiveCancellationOwned();
            }
            else if (!PerformanceSessionStore.HasActiveSnapshot)
            {
                PerformanceSessionStore.ClearActiveCancellationOwned();
            }
        }

        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        finally
        {
            ReleaseActiveOwner();
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return save ? session : null;
    }

    public void Cancel() => Stop(save: false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (IsRunning)
        {
            try
            {
                Stop(save: false);
            }
            catch
            {
                // Dispose is used by application shutdown and must not abort
                // exit. Stop has already released the sampler and owner; when
                // the durable cancel tombstone failed it deliberately leaves
                // _active.json in place for the next launch to recover.
            }
        }
        _sampler?.Dispose();
        _sampler = null;
        ReleaseActiveOwner();
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
        // 显存容量在会话内是常量：取首个有效样本的容量入库，供离线洞察计算占比
        var vramTotalRaw = buffer.Select(s => s.VramTotalBytes).FirstOrDefault(t => t is { } v && v > 0);
        var vramTotalMib = vramTotalRaw is { } total
            ? Math.Round(total / 1024.0 / 1024.0, 1)
            : (double?)null;
        return new PerformanceSession(
            PerformanceSessionStore.NewSessionId(),
            _name,
            _startedAt,
            endedAt,
            PerformanceSessionStore.CurrentSchemaVersion,
            CurrentInterval.TotalSeconds,
            points,
            vramTotalMib);
    }

    private void OnSample(MetricSample sample)
    {
        LatestSample = sample;
        _sampleCount++;
        if (_sampleCount % AutosaveEverySamples == 0 && _sampler is not null)
        {
            var snapshot = ToSession(DateTime.Now) with { Id = "active-snapshot" };
            PerformanceSessionStore.SaveActiveSnapshotOwned(snapshot);
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

    private void ReleaseActiveOwner()
    {
        var owner = _activeOwner;
        _activeOwner = null;
        owner?.Dispose();
    }
}
