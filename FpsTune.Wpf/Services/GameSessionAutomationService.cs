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

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, GameProcessRun?> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DisplayMode> _refreshBackup = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = "";
    private bool _refreshEngaged;
    private int _polling;
    private int _disposed;

    // 测试注入；null 时走真实游戏库 / 进程探测 / 显示 API / 准备执行。
    internal Func<IReadOnlyList<StateStore.GameProfile>>? GamesProvider { get; set; }
    internal Func<string, GameProcessObservation>? Probe { get; set; }
    internal IDisplayModeApi? ModeApi { get; set; }
    internal Func<IReadOnlyList<IccDisplayInfo>>? DisplaysProvider { get; set; }
    internal Func<StateStore.GameProfile, GamePreparationOptions>? PreparationResolver { get; set; }
    internal Func<GamePreparationOptions, string, Task<IReadOnlyList<PreparationStep>>>? PreparationRunner { get; set; }

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
            var games = GamesProvider?.Invoke() ?? GameLibraryService.Read();
            var signature = $"{prepEnabled}|{refreshEnabled}|{string.Join("|", games.Select(g => g.ExePath.ToUpperInvariant()))}";
            var configChanged = !string.Equals(_signature, signature, StringComparison.Ordinal);
            _signature = signature;

            var observations = new Dictionary<string, GameProcessObservation>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games)
                observations[game.ExePath] = (Probe ?? AutoRecordingService.ObserveProcess)(game.ExePath);

            // 基线：配置变化或首次观察不触发边沿（应用启动时游戏已在运行不算"启动"）。
            var edges = new List<(StateStore.GameProfile Game, bool Started)>();
            var restoreOnDisable = false;
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
            }
            if (restoreOnDisable) DisengageRefresh(Str.T("Str.AutoSessionRefreshReasonToggle"));

            foreach (var (game, started) in edges)
            {
                if (started)
                {
                    if (prepEnabled) _ = RunPreparationAsync(game);
                    if (refreshEnabled) EngageRefresh();
                }
            }

            // 全部游戏退出后还原刷新率（含开关关闭或游戏库清空的场景）。
            var anyRunning = observations.Any(o => o.Value.Run is not null);
            if (!anyRunning) DisengageRefresh(Str.T("Str.AutoSessionRefreshReasonAllGames"));
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
                Audit(after.Hz == mode.Hz
                    ? Str.T("Str.AutoSessionRefreshOff", display.FriendlyName, mode.Hz)
                    : Str.T("Str.AutoSessionRefreshFailed", display.FriendlyName));
            }
            catch (Exception ex)
            {
                Audit(Str.T("Str.AutoSessionRefreshFailed", Str.T("Str.AutoSessionRefreshError", display.FriendlyName, PrivacyScrub.Sanitize(ex.Message))));
            }
        }
        lock (_gate)
        {
            _refreshBackup.Clear();
            _refreshEngaged = false;
        }
        Audit(Str.T("Str.AutoSessionRefreshReason", reason));
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
    }
}
