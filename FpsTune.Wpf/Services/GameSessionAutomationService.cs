using System.IO;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 游戏会话自动化（均为 opt-in，默认关闭）：
/// ① 游戏启动边沿自动执行该游戏已保存的开局准备方案；
/// ② 游戏启动时把显示器切到当前分辨率下的最高刷新率，全部游戏退出后还原。
/// 进程探测复用 AutoRecordingService.ObserveProcess（完整路径身份校验，会话过滤）；
/// 只用边沿触发，不做持续监控写操作；变化写入 AutoProfile 活动中心。
/// </summary>
internal sealed class GameSessionAutomationService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    /// <summary>刷新率还原部分失败后的重试退避（测试可缩短）。</summary>
    internal static TimeSpan RefreshRetryBackoff = TimeSpan.FromSeconds(10);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, GameProcessRun?> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DisplayMode> _refreshBackup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (nuint Affinity, uint Priority, bool AffinityWritten, bool PriorityWritten, DateTime StartedAt)> _affinityTouched = new();
    private string _signature = "";
    private bool _refreshEngaged;
    private bool _affinityEngaged;
    private DateTime _nextRefreshRetryAt;
    private int _polling;
    private int _disposed;

    // 测试注入；null 时走真实游戏库 / 进程探测 / 显示 API / 准备执行。
    internal Func<IReadOnlyList<StateStore.GameProfile>>? GamesProvider { get; set; }
    internal Func<string, GameProcessObservation>? Probe { get; set; }
    internal IDisplayModeApi? ModeApi { get; set; }
    internal Func<IReadOnlyList<IccDisplayInfo>>? DisplaysProvider { get; set; }
    internal Func<StateStore.GameProfile, GamePreparationOptions>? PreparationResolver { get; set; }
    internal Func<GamePreparationOptions, string, Task<IReadOnlyList<PreparationStep>>>? PreparationRunner { get; set; }
    internal Func<ushort[]>? EfficiencyClassesProvider { get; set; }
    internal Func<string, IReadOnlyList<int>>? PidsByPathOverride { get; set; }
    /// <summary>进程启动时间读取（PID 复用身份核对）；测试注入。返回 null = 进程已不在。</summary>
    internal Func<int, DateTime?>? ProcessStartedAtOverride { get; set; }

    public GameSessionAutomationService()
        => _timer = new Timer(TimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _timer.Change(TimeSpan.Zero, PollInterval);
        }
    }

    /// <summary>开关变化后立即重估状态并重置边沿基线（设置页保存时调用）。</summary>
    internal void ApplySettings()
    {
        lock (_gate)
        {
            _observed.Clear();
            _signature = "";
        }
        Poll();
    }

    private void TimerTick(object? state) => Poll();

    internal void Poll()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            var settings = SettingsService.Current;
            var prepEnabled = settings.AutoPreparationEnabled;
            var refreshEnabled = settings.AutoRefreshBoostEnabled;
            var affinityEnabled = settings.AutoAffinityIsolateEnabled;
            var games = GamesProvider?.Invoke() ?? GameLibraryService.Read();
            var signature = $"{prepEnabled}|{refreshEnabled}|{affinityEnabled}|{string.Join("|", games.Select(g => g.ExePath.ToUpperInvariant()))}";
            var configChanged = !string.Equals(_signature, signature, StringComparison.Ordinal);
            _signature = signature;

            var observations = new Dictionary<string, GameProcessObservation>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games)
                observations[game.ExePath] = (Probe ?? AutoRecordingService.ObserveProcess)(game.ExePath);

            // 基线：配置变化或首次观察不触发边沿（应用启动时游戏已在运行不算"启动"）。
            var edges = new List<(StateStore.GameProfile Game, bool Started)>();
            var restoreOnDisable = false;
            var restoreAffinityOnDisable = false;
            lock (_gate)
            {
                foreach (var (path, observation) in observations)
                {
                    if (!observation.Reliable) continue; // 探测不可靠不是边沿
                    var known = _observed.TryGetValue(path, out var before);
                    if (configChanged || !known)
                    {
                        _observed[path] = observation.Run;
                        continue;
                    }
                    var started = before is null && observation.Run is not null;
                    var stopped = before is not null && observation.Run is null;
                    if (started || stopped) _observed[path] = observation.Run;
                    var game = games.First(g => g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (started) edges.Add((game, true));
                    if (stopped) edges.Add((game, false));
                }
                // 运行中关闭刷新开关：立即还原已切换的显示器，不留挂起状态。
                restoreOnDisable = configChanged && !refreshEnabled && _refreshEngaged;
                restoreAffinityOnDisable = configChanged && !affinityEnabled && _affinityEngaged;
            }
            if (restoreOnDisable) DisengageRefresh(Str.T("Str.AutoSessionRefreshReasonToggle"));
            if (restoreAffinityOnDisable) DisengageAffinity(Str.T("Str.AutoSessionRefreshReasonToggle"));

            foreach (var (game, started) in edges)
            {
                if (started)
                {
                    if (prepEnabled) _ = RunPreparationAsync(game);
                    if (refreshEnabled) EngageRefresh();
                }
            }

            // 后台让路：游戏运行期间持续应用（覆盖新启动的进程），全部退出后还原。
            if (affinityEnabled && observations.Any(o => o.Value.Run is not null))
                ReapplyAffinity();
            else if (_affinityEngaged)
                DisengageAffinity(Str.T("Str.AutoSessionRefreshReasonAllGames"));

            // 全部游戏退出后还原刷新率（含开关关闭或游戏库清空的场景）；部分失败按退避重试。
            var anyRunning = observations.Any(o => o.Value.Run is not null);
            if (!anyRunning && DateTime.UtcNow >= _nextRefreshRetryAt)
                DisengageRefresh(Str.T("Str.AutoSessionRefreshReasonAllGames"));
        }
        catch (Exception ex)
        {
            Audit(Str.T("Str.AutoSessionAuditFailed", PrivacyScrub.Sanitize(ex.Message)));
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private async Task RunPreparationAsync(StateStore.GameProfile game)
    {
        try
        {
            var options = PreparationResolver?.Invoke(game) ?? PreparationPlanStore.Resolve(game.ExePath).Options;
            if (!options.Configured || !PreparationPlanStore.Valid(options))
            {
                Audit(Str.T("Str.AutoSessionPrepSkipped", game.Name, Str.T("Str.AutoSessionPrepNoPlan")));
                return;
            }
            var service = new GamePreparationService();
            if (service.NeedsAdministrator(options) && !AdminHelper.IsAdministrator())
            {
                Audit(Str.T("Str.AutoSessionPrepSkipped", game.Name, Str.T("Str.AutoSessionPrepNoAdmin")));
                return;
            }
            var results = await (PreparationRunner?.Invoke(options, game.ExePath)
                ?? service.RunAsync(options, gamePath: game.ExePath));
            var ok = results.All(r => r.Succeeded);
            Audit(Str.T(ok ? "Str.AutoSessionPrepDone" : "Str.AutoSessionPrepPartial",
                game.Name, results.Count(r => r.Succeeded), results.Count));
        }
        catch (Exception ex)
        {
            Audit(Str.T("Str.AutoSessionPrepFailed", game.Name, PrivacyScrub.Sanitize(ex.Message)));
        }
    }

    private void EngageRefresh()
    {
        var api = ModeApi ?? new WindowsDisplayModeApi();
        var displays = DisplaysProvider?.Invoke() ?? IndependentDisplays();
        foreach (var display in displays)
        {
            try
            {
                lock (_gate)
                {
                    if (_refreshEngaged && _refreshBackup.ContainsKey(display.GdiDeviceName)) continue;
                }
                var current = api.Read(display);
                var target = PickTarget(current, api.Modes(display));
                if (target is null) continue; // 已是最高刷新率
                api.Apply(display, target);
                if (api.Read(display).Hz != target.Hz)
                {
                    api.Apply(display, current); // 读回不符立即还原，不留中间态
                    Audit(Str.T("Str.AutoSessionRefreshFailed", display.FriendlyName));
                    continue;
                }
                lock (_gate)
                {
                    _refreshBackup[display.GdiDeviceName] = current;
                    _refreshEngaged = true;
                }
                Audit(Str.T("Str.AutoSessionRefreshOn", display.FriendlyName, target.Hz));
            }
            catch (Exception ex)
            {
                Audit(Str.T("Str.AutoSessionRefreshFailed", Str.T("Str.AutoSessionRefreshError", display.FriendlyName, PrivacyScrub.Sanitize(ex.Message))));
            }
        }
    }

    private void DisengageRefresh(string reason)
        => DisengageRefreshCore(ModeApi ?? new WindowsDisplayModeApi(),
            DisplaysProvider?.Invoke() ?? IndependentDisplays(), reason);

    private void DisengageRefreshCore(IDisplayModeApi api, IReadOnlyList<IccDisplayInfo> displays, string reason)
    {
        List<KeyValuePair<string, DisplayMode>> backups;
        lock (_gate)
        {
            if (!_refreshEngaged || _refreshBackup.Count == 0) return;
            backups = _refreshBackup.ToList();
        }
        // 只清成功恢复的设备：显示器暂时不在拓扑 / Apply 失败的条目保留在备份里按退避重试。
        // 原实现无条件清空并把 _refreshEngaged 置假——失败设备停在提升后的刷新率，
        // 内部状态与审计却声称「已还原」，且原模式数据已丢、没有重试路径。
        var restoredDevices = new List<string>();
        foreach (var (device, mode) in backups)
        {
            var display = displays.FirstOrDefault(d => string.Equals(d.GdiDeviceName, device, StringComparison.OrdinalIgnoreCase));
            if (display is null)
            {
                Audit(Str.T("Str.AutoSessionRefreshRestoreMissing", device));
                continue;
            }
            try
            {
                api.Apply(display, mode);
                var after = api.Read(display);
                if (after.Hz != mode.Hz)
                {
                    Audit(Str.T("Str.AutoSessionRefreshFailed", display.FriendlyName));
                    continue;
                }
                restoredDevices.Add(device);
                Audit(Str.T("Str.AutoSessionRefreshOff", display.FriendlyName, mode.Hz));
            }
            catch (Exception ex)
            {
                Audit(Str.T("Str.AutoSessionRefreshFailed", Str.T("Str.AutoSessionRefreshError", display.FriendlyName, PrivacyScrub.Sanitize(ex.Message))));
            }
        }
        lock (_gate)
        {
            foreach (var device in restoredDevices)
                _refreshBackup.Remove(device);
            if (_refreshBackup.Count == 0)
            {
                _refreshEngaged = false;
                _nextRefreshRetryAt = DateTime.MinValue;
            }
            else
            {
                _nextRefreshRetryAt = DateTime.UtcNow.Add(RefreshRetryBackoff);
            }
        }
        Audit(Str.T("Str.AutoSessionRefreshReason", reason));
    }

    private void ReapplyAffinity()
    {
        var classes = EfficiencyClassesProvider?.Invoke() ?? CpuTopology.EfficiencyClasses();
        var (_, backgroundMask) = CpuTopology.SplitMasks(classes);
        var paths = AutoRecordingService.NormalizePaths(SettingsService.Current.MemoryBackgroundApps);
        if (paths.Length == 0)
        {
            if (_affinityEngaged) DisengageAffinity(Str.T("Str.AutoSessionAffinityNoApps"));
            return;
        }
        var touchedNew = 0;
        foreach (var path in paths)
        {
            foreach (var pid in PidsByPath(path))
            {
                lock (_gate)
                {
                    if (_affinityTouched.ContainsKey(pid)) continue;
                }
                var original = NativeProcessScheduling.Read(pid);
                if (original is not { } before) continue; // 受保护/已退出：如实跳过
                var affinityWritten = false;
                var priorityWritten = false;
                if (backgroundMask != 0 && before.Affinity != backgroundMask)
                    affinityWritten = NativeProcessScheduling.WriteAffinity(pid, backgroundMask);
                // 只把"常规或更高"优先级往下一档；已是低优先级的不动。
                if (before.Priority is 0x20 or 0x8000 or 0x80)
                    priorityWritten = NativeProcessScheduling.WritePriority(pid, NativeProcessScheduling.PriorityBelowNormal);
                if (!affinityWritten && !priorityWritten) continue;
                // 记录启动时间作为进程身份：还原前核对，PID 被复用就不写。
                var startedAt = ProcessStartedAtOverride?.Invoke(pid) ?? TryReadProcessStart(pid) ?? DateTime.MinValue;
                lock (_gate)
                {
                    _affinityTouched[pid] = (before.Affinity, before.Priority, affinityWritten, priorityWritten, startedAt);
                    _affinityEngaged = true;
                }
                touchedNew++;
            }
        }
        if (touchedNew > 0)
            Audit(Str.T("Str.AutoSessionAffinityOn", touchedNew));
    }

    private void DisengageAffinity(string reason)
    {
        List<KeyValuePair<int, (nuint Affinity, uint Priority, bool AffinityWritten, bool PriorityWritten, DateTime StartedAt)>> touched;
        lock (_gate)
        {
            if (!_affinityEngaged || _affinityTouched.Count == 0)
            {
                _affinityEngaged = false;
                return;
            }
            touched = _affinityTouched.ToList();
            _affinityTouched.Clear();
            _affinityEngaged = false;
        }
        var restored = 0;
        foreach (var (pid, original) in touched)
        {
            try
            {
                // PID 可能已被新进程复用：启动时间对不上就绝不写——把旧掩码/低优先级写到
                // 无关新进程（甚至用户刚启动的游戏）上正是本功能要避免的现象。
                var currentStart = ProcessStartedAtOverride?.Invoke(pid) ?? TryReadProcessStart(pid);
                if (currentStart is null || original.StartedAt == DateTime.MinValue || currentStart != original.StartedAt)
                    continue;
                var ok = true;
                if (original.AffinityWritten) ok &= NativeProcessScheduling.WriteAffinity(pid, original.Affinity);
                if (original.PriorityWritten) ok &= NativeProcessScheduling.WritePriority(pid, original.Priority);
                if (ok) restored++; // 写失败如实计入审计：不把失败说成已还原
            }
            catch { /* 进程已退出：无需还原 */ }
        }
        Audit(Str.T("Str.AutoSessionAffinityOff", restored, reason));
    }

    private static DateTime? TryReadProcessStart(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.StartTime; }
        catch { return null; }
    }

    private IReadOnlyList<int> PidsByPath(string path)
    {
        if (PidsByPathOverride is not null) return PidsByPathOverride(path);
        var result = new List<int>();
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0) return result;
        System.Diagnostics.Process[]? processes = null;
        try
        {
            processes = System.Diagnostics.Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var image = ProcessIdentity.TryGetImagePath(process);
                    if (image is not null && image.Equals(path, StringComparison.OrdinalIgnoreCase))
                        result.Add(process.Id);
                }
                catch { /* 探测单个进程失败跳过 */ }
            }
        }
        catch { /* 枚举失败返回空 */ }
        finally
        {
            if (processes is not null)
                foreach (var process in processes)
                    process.Dispose();
        }
        return result;
    }

    internal static DisplayMode? PickTarget(DisplayMode current, IReadOnlyList<DisplayMode> modes)
        => modes.Where(m => m.Width == current.Width && m.Height == current.Height && m.Hz > current.Hz)
            .OrderByDescending(m => m.Hz).FirstOrDefault();

    private static IReadOnlyList<IccDisplayInfo> IndependentDisplays()
        => DisplayTopology.GetDisplays().Where(d => d.Independent).ToArray();

    private static void Audit(string detail)
        => AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now, AutoProfileActivityStore.KindInfo, "", null, detail));

    public void Dispose()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Dispose();
        }
        DisengageRefreshCore(new WindowsDisplayModeApi(), IndependentDisplays(), Str.T("Str.AutoSessionRefreshReasonExit"));
        DisengageAffinity(Str.T("Str.AutoSessionRefreshReasonExit"));
    }
}
