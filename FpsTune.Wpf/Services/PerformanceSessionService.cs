using System.IO;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

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
    private readonly Func<TimeSpan, int, MetricsSampler> _createSampler;
    private readonly Func<SessionFpsRecorder> _createFpsRecorder;
    private SessionFpsRecorder? _fpsRecorder;
    private string _name = "";
    private DateTime _startedAt;
    private int _sampleCount;
    private int _disposed;
    private IDisposable? _activeOwner;
    private Task<PerformanceSession?>? _stopTask;
    private readonly Stopwatch _clock = new();
    public double SampleIntervalSeconds { get; private set; }
    public SessionConditions? Conditions { get; private set; }
    public event Action<string>? AutomaticStopFailed;

    public bool IsRunning { get; private set; }
    public bool IsStopping { get; private set; }
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

    public PerformanceSessionService() : this((interval, capacity) => new MetricsSampler(interval, capacity), () => new SessionFpsRecorder()) { }
    internal PerformanceSessionService(Func<TimeSpan, int, MetricsSampler> createSampler, Func<SessionFpsRecorder> createFpsRecorder)
    { _createSampler = createSampler; _createFpsRecorder = createFpsRecorder; }

    /// <summary>运行中最近一次各指标不可用原因（空字典 = 全部可用）。</summary>
    public IReadOnlyDictionary<string, string> CurrentUnavailableReasons
        => _sampler?.UnavailableReasons ?? new Dictionary<string, string>();

    /// <summary>0.2.0 C-E：运行中 FPS 采集的不可用原因资源键（null = FPS 正在记录）。</summary>
    public string? CurrentFpsUnavailableReasonKey => _fpsRecorder?.UnavailableReasonKey;

    /// <summary>运行中已缓冲的样本（只读快照语义，UI 线程使用）。</summary>
    public IReadOnlyList<MetricSample> RunningBuffer => _sampler?.Buffer ?? Array.Empty<MetricSample>();

    public bool Start(string name, SessionConditions? conditions = null)
    {
        if (GamePreparationService.IsRunning) throw new InvalidOperationException(Str.T("Str.PrepareBusy"));
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PerformanceSessionService));
        if (IsRunning)
            return false;
        var captured = SessionConditionRules.CaptureGame(AppState.GamePath, conditions);
        if (!SessionConditionRules.IsValid(captured))
            throw new ArgumentException(Str.T("Str.ConditionInvalid"), nameof(conditions));

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
            Conditions = captured;
            _clock.Restart();
            _sampleCount = 0;
            var interval = CurrentInterval;
            SampleIntervalSeconds = interval.TotalSeconds;
            _sampler = _createSampler(interval, CurrentBufferCapacity);
            _sampler.Sampled += OnSample;
            // 0.2.0 C-E：会话期间同步跑 PresentMon（可选能力，失败降级并记录原因键）。
            _fpsRecorder = _createFpsRecorder();
            _fpsRecorder.Start(AppState.GamePath);
            IsRunning = true;
            _sampler.Start();
            StateChanged?.Invoke();
            return true;
        }
        catch
        {
            IsRunning = false;
            _clock.Stop();
            _fpsRecorder?.Stop();
            _fpsRecorder = null;
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
        if (IsStopping) throw new InvalidOperationException(Str.T("Str.SessionSaving"));
        if (!IsRunning) return null;
        var (session, recorder) = BeginStop();
        return CompleteStop(session, recorder, save);
    }

    /// <summary>在创建线程冻结采样快照，后台结束 FPS 与写盘；重复请求等待同一次保存。</summary>
    public Task<PerformanceSession?> StopAsync(bool save = true)
    {
        if (IsStopping) return _stopTask!;
        if (!IsRunning) return Task.FromResult<PerformanceSession?>(null);
        var (session, recorder) = BeginStop();
        _stopTask = CompleteStopAsync(session, recorder, save);
        StateChanged?.Invoke();
        return _stopTask;
    }

    private async Task<PerformanceSession?> CompleteStopAsync(PerformanceSession session, SessionFpsRecorder? recorder, bool save)
    {
        try { return await Task.Run(() => CompleteStop(session, recorder, save, notify: false)); }
        finally { StateChanged?.Invoke(); }
    }

    private (PerformanceSession Session, SessionFpsRecorder? Recorder) BeginStop()
    {
        var sampler = _sampler;
        sampler?.Stop();
        if (sampler is not null) sampler.Sampled -= OnSample;
        var session = ToSession(DateTime.Now);
        var recorder = _fpsRecorder;
        IsStopping = true;
        _clock.Stop();
        // Dispose only touches the timer on this thread; counter disposal is already deferred.
        sampler?.Dispose();
        _sampler = null;
        _fpsRecorder = null;
        LatestSample = null;
        return (session, recorder);
    }

    private PerformanceSession? CompleteStop(PerformanceSession session, SessionFpsRecorder? recorder, bool save, bool notify = true)
    {
        Exception? failure = null;
        try
        {
            // Keep the final hardware samples recoverable even if FPS draining or saving is interrupted.
            if (save) PerformanceSessionStore.SaveActiveSnapshotOwned(session with { Id = "active-snapshot" });
            if (recorder is not null)
            {
                var fps = recorder.Stop(parse: save);
                session = session with { Fps = fps, FpsNote = fps is null ? recorder.UnavailableReasonKey : null };
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        if (save && failure is null)
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
            IsRunning = false;
            IsStopping = false;
            if (notify) StateChanged?.Invoke();
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
        // A normal window close awaits StopAsync. Never release its ownership underneath a pending save.
        if (IsStopping) return;
        if (IsRunning)
        {
            try
            {
                Stop(save: true);
            }
            catch
            {
                // Dispose is used by application shutdown and must not abort
                // exit. Saving failures leave the active snapshot for startup recovery.
            }
        }
        _sampler?.Dispose();
        _sampler = null;
        ReleaseActiveOwner();
    }

    private PerformanceSession ToSession(DateTime endedAt, SessionFpsStats? fps = null, string? fpsNote = null)
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
            SampleIntervalSeconds,
            points,
            vramTotalMib,
            fps,
            fpsNote,
            Conditions);
    }

    private async void OnSample(MetricSample sample)
    {
        LatestSample = sample;
        _sampleCount++;
        if (_sampleCount % AutosaveEverySamples == 0 && _sampler is not null)
        {
            // 快照不带 FPS（终值在 Stop 时统一解析）；FPS 不可用原因随快照落库，恢复会话可明示。
            var snapshot = ToSession(DateTime.Now) with
            {
                Id = "active-snapshot",
                FpsNote = _fpsRecorder?.UnavailableReasonKey,
            };
            PerformanceSessionStore.SaveActiveSnapshotOwned(snapshot);
        }
        Sampled?.Invoke(sample);
        if (IsRunning && !IsStopping && Conditions?.TargetSeconds is { } target && _clock.Elapsed.TotalSeconds >= target)
        {
            try { await StopAsync(save: true); }
            catch (Exception ex) { AutomaticStopFailed?.Invoke(ex.Message); }
        }
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
