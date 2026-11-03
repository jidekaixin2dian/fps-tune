using System.Diagnostics;
using System.IO;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record GameTimerStatus(bool Enabled, bool Active, string? Game, uint CurrentResolution);

/// <summary>
/// 游戏运行时保持 0.5 ms 计时器分辨率请求（opt-in，默认关闭）。
/// 复用 AutoProfile 的安全姿态：只按进程名轮询 + 完整路径身份校验，不读进程内存。
/// 请求与撤销都即时生效且可逆；活动变化写入 AutoProfile 活动中心（KindInfo）。
/// </summary>
internal sealed class GameTimerResolutionService : IDisposable
{
    public const uint TargetResolution = 5000; // 0.5 ms，100ns 单位
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _active;
    private string? _activeGame;
    private int _polling;
    private int _disposed;

    // 测试注入；null 时走真实游戏库与进程探测。
    internal Func<IReadOnlyList<StateStore.GameProfile>>? GamesProvider { get; set; }
    internal Func<string, string?, bool>? RunningProbe { get; set; }

    public GameTimerResolutionService()
        => _timer = new Timer(TimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed != 0 || _stop.IsCancellationRequested) return;
            _timer.Change(TimeSpan.Zero, PollInterval);
        }
    }

    /// <summary>开关变化后立即重估状态（设置页保存时调用）。</summary>
    internal void ApplySettings() => Poll();

    private void TimerTick(object? state) => Poll();

    internal void Poll()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _stop.IsCancellationRequested) return;
            var settings = SettingsService.Current;
            if (!settings.GameTimerBoostEnabled)
            {
                Release(Str.T("Str.TimerReasonDisabled"));
                return;
            }
            var games = GamesProvider?.Invoke() ?? LibraryGames();
            if (games.Count == 0)
            {
                Release(Str.T("Str.TimerReasonNoGame"));
                return;
            }
            var probe = RunningProbe ?? DefaultProbe;
            string? running = null;
            foreach (var game in games)
            {
                // Process.GetProcessesByName 匹配的是不带扩展名的 ProcessName；
                // 传完整文件名（xxx.exe）永远查不到进程，开关就成了摆设。
                if (!probe(Path.GetFileNameWithoutExtension(game.ExePath), game.ExePath)) continue;
                running = game.Name;
                break;
            }
            if (running is null)
            {
                Release(Str.T("Str.TimerReasonGameClosed"));
                return;
            }
            Acquire(running);
        }
        catch (Exception ex)
        {
            // 探测/写入失败保持现状（sticky），把原因交给活动中心而不是静默吞掉。
            TryAudit(Str.T("Str.TimerAuditFailed", PrivacyScrub.Sanitize(ex.Message)));
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void Acquire(string gameName)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (_active)
            {
                _activeGame = gameName;
                return;
            }
            var current = NativeTimerResolution.Set(TargetResolution, enable: true);
            _active = true;
            _activeGame = gameName;
            TryAudit(Str.T("Str.TimerAuditAcquired", gameName, current / 10000.0));
        }
    }

    private void Release(string reason)
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_active) return;
            NativeTimerResolution.Set(TargetResolution, enable: false);
            _active = false;
            _activeGame = null;
            TryAudit(Str.T("Str.TimerAuditReleased", reason));
        }
    }

    internal GameTimerStatus Status
    {
        get
        {
            lock (_gate)
            {
                uint current = 0;
                try { current = NativeTimerResolution.Query().Current; }
                catch { /* 读不到保持 0，界面按不可用显示 */ }
                return new(SettingsService.Current.GameTimerBoostEnabled, _active, _activeGame, current);
            }
        }
    }

    private static IReadOnlyList<StateStore.GameProfile> LibraryGames() => GameLibraryService.Read();

    internal static bool DefaultProbe(string processName, string? exePath)
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
                if (ProcessIdentity.MatchesPath(process, exePath))
                    return true;
            return false;
        }
        finally
        {
            if (processes is not null)
                foreach (var process in processes)
                    process.Dispose();
        }
    }

    private static void TryAudit(string detail)
        => AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now, AutoProfileActivityStore.KindInfo, "", null, detail));

    public void Dispose()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Dispose();
            if (_active)
            {
                try { NativeTimerResolution.Set(TargetResolution, enable: false); } catch { /* 退出时撤销失败不影响关闭 */ }
                _active = false;
                _activeGame = null;
            }
            _stop.Cancel();
        }
    }
}
