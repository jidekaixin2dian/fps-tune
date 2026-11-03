using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>独立游戏方案状态。Detail 为可直接展示的短诊断，不含完整路径。</summary>
public sealed record FilterWatcherStatus(
    bool Enabled,
    string? ActiveProcess,
    string? ActivePreset,
    string Detail = "",
    bool Suppressed = false,
    int? Vibrance = null,
    bool ScreenIncluded = false,
    string? LastError = null,
    bool Paused = false,
    bool ActiveAuto = false);

/// <summary>前台探针接口：进程身份、路径、标题及所在屏可由 fake 替代。</summary>
internal interface IPerAppForegroundProbe
{
    ForegroundAppInfo? Read();
    string? DisplayIdentity { get; }
}

/// <summary>前台跟随只负责调度和诊断；写入/读回/还原统一交给 PerAppFilterExecution。</summary>
internal sealed class ForegroundFilterWatcher : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private readonly IPerAppForegroundProbe _probe;
    private readonly PerAppFilterStateMachine _state = new();
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _applied;
    private FilterAppRule? _appliedRule;
    private string? _appliedDisplay;
    private int _appliedPid;
    private ForegroundAppInfo? _active;
    private string _detail = "";
    private string? _lastError;
    private DateTime _nextRetryAt;
    private int _polling;
    private int _disposed;
    private bool _paused;
    private DateTime _fullscreenCheckedAt;
    private bool _fullscreen;

    internal static Func<ForegroundAppInfo?>? ForegroundProcessOverride { get; set; }
    internal static Func<string?, bool>? ApplyOverride { get; set; }
    internal static Func<bool>? RestoreOverride { get; set; }
    internal static Func<string?>? DisplayOverride { get; set; }
    internal static Func<int, bool>? ProcessAliveOverride { get; set; }

    public ForegroundFilterWatcher() : this(null) { }
    internal ForegroundFilterWatcher(IPerAppForegroundProbe? probe = null)
    {
        _probe = probe ?? new Win32PerAppForegroundProbe();
        _timer = new Timer(TimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        try { PerAppFilterExecution.RestoreOnStartup(); }
        catch (Exception ex) { SetError(ex.Message); }
        lock (_gate) { if (_disposed != 0) return; }
        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    internal void ApplySettings()
    {
        _state.Reset();
        Poll();
    }

    /// <summary>界面上的「停止」：立刻停下并还原，开关保持开着；再次开启跟随（或点「重新启动」）恢复。</summary>
    internal void Pause()
    {
        _paused = true;
        _state.Reset();
        if (_applied) Disengage(Str.T("Str.FilterWatcherReasonStopped"), false);
        SetDetail(Str.T("Str.FilterWatcherStopped"));
    }

    internal void Resume()
    {
        _paused = false;
        _state.Reset();
        Poll();
    }

    internal bool Paused => _paused;
    private void TimerTick(object? state) => Poll();

    /// <summary>没有规则命中时，按「全自动」模板与范围判断要不要当场套上（「没收录也自动应用」）。</summary>
    private FilterAppRule? FallbackRule(ForegroundAppInfo app, AppSettings settings)
    {
        if (!settings.FilterAutoFallbackEnabled) return null;
        if (FilterSchemeStore.AutoTemplate is not { PresetFileName.Length: > 0 } template) return null;
        var scope = PerAppAutoScopeRules.Parse(settings.FilterAutoScope);
        var fullscreen = scope == PerAppAutoScope.Fullscreen && ForegroundFullscreen();
        if (!PerAppAutoScopeRules.Matches(scope, app.ExePath, fullscreen)) return null;
        return new FilterAppRule(app.ProcessName, template.PresetFileName, template.Vibrance, template.IncludeScreen,
            app.ExePath, "", true, FilterAppMatchKind.Path, Auto: true, Screen: template.Screen);
    }

    /// <summary>全屏判定要枚举显示拓扑，不能每 500ms 都跑；一秒内的重复询问用缓存值。</summary>
    private bool ForegroundFullscreen()
    {
        var now = DateTime.UtcNow;
        if (now - _fullscreenCheckedAt < TimeSpan.FromSeconds(1)) return _fullscreen;
        _fullscreenCheckedAt = now;
        _fullscreen = PerAppAutoScopeRules.IsForegroundFullscreen();
        return _fullscreen;
    }

    internal void Poll()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            var settings = SettingsService.Current;
            var enabled = settings.FilterWatcherEnabled;
            var suppressed = FullscreenPresetWatcher.SuppressesForegroundRules;
            if (_paused)
            {
                _state.Reset();
                Disengage(Str.T("Str.FilterWatcherReasonStopped"), false);
                SetDetail(Str.T("Str.FilterWatcherStopped"));
                return;
            }
            if (!enabled || suppressed)
            {
                _state.Reset();
                Disengage(Str.T(suppressed ? "Str.FullscreenReasonTakeover" : "Str.FilterWatcherReasonOff"), suppressed);
                SetDetail(Str.T(suppressed ? "Str.FilterWatcherSuppressed" : "Str.FilterWatcherIdle"));
                return;
            }
            // 游戏已经退出但 GetForegroundWindow 仍可能返回失效窗口，不能把“窗口没了”当 Unknown
            // 永久保留。按已应用进程 ID 明确识别退出边沿，先还原再继续本轮。
            if (_applied && _appliedPid > 0 && !IsProcessAlive(_appliedPid))
            {
                _state.Reset();
                Disengage(Str.T("Str.FilterWatcherReasonExit"), false);
                return;
            }
            var app = ReadForeground();
            var matched = IccAppRuleStore.Match(app);
            // 命中但什么都没设的规则＝「跟随桌面」：不套模板、也不写屏，就当它不存在但也不让全自动兜底接管。
            var rule = matched is { HasEffect: true } ? matched : null;
            if (rule is null && app is not null && matched is null) rule = FallbackRule(app, settings);
            var display = ReadDisplay();
            lock (_gate) _active = app;
            // 路径/标题读失败保持当前状态，不把短暂 Unknown 当成退出。
            if (app is null && _applied) return;
            // 关掉「切出游戏就恢复」＝跟进程走：切到桌面、微信、网页都保持，只有游戏进程退出才还原。
            if (rule is null && _applied && !settings.FilterRestoreOnFocusLoss
                && !(_appliedPid > 0 && !IsProcessAlive(_appliedPid)))
            {
                SetDetail(Str.T("Str.FilterWatcherKeeping", _appliedRule?.ProcessName ?? ""));
                return;
            }
            var decision = _state.Update(rule, rule is null ? null : display);
            if (decision.Action == PerAppFilterAction.Restore)
                Disengage(Str.T("Str.FilterWatcherReasonLeave"), false);
            else if (decision.Action == PerAppFilterAction.Apply && rule is not null && display is not null
                && DateTime.UtcNow >= _nextRetryAt)
                Engage(display, rule, app!);
            else if (rule is not null && display is null)
                SetError(Str.T("Str.DisplayDisconnected"));
            else
                SetDetail(rule is null ? Str.T("Str.FilterWatcherNoMatch") : Str.T("Str.FilterWatcherDebouncing"));
        }
        catch (Exception ex)
        {
            // 写入失败时状态机已推进；清空并退避，避免永远认为"已应用"或每秒刷日志。
            _state.Reset();
            _nextRetryAt = DateTime.UtcNow.AddSeconds(10);
            SetError(ex.Message);
            Record(AutoProfileActivityStore.KindFailed, Str.T("Str.FilterWatcherFailed", PrivacyScrub.Sanitize(ex.Message)));
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private void Engage(string display, FilterAppRule rule, ForegroundAppInfo app)
    {
        lock (_gate)
        {
            if (_applied && _appliedRule?.Key == rule.Key && _appliedDisplay == display) return;
        }
        // 换屏/换方案先恢复上次自动方案；失败保留旧凭证，不在未知原值上再写。
        if (_applied) Disengage(Str.T("Str.FilterWatcherReasonRetarget"), false);
        string outcome;
        if (ApplyOverride is { } hook)
        {
            if (!hook(rule.PresetFileName)) throw new InvalidOperationException(Str.T("Str.DisplayWriteUnconfirmed"));
            outcome = rule.PresetFileName;
        }
        else outcome = PerAppFilterExecution.Apply(display, rule);
        lock (_gate)
        {
            _applied = true; _appliedRule = rule; _appliedDisplay = display; _appliedPid = app.ProcessId;
            _lastError = null;
            _detail = Str.T("Str.FilterWatcherActive", app.ProcessName, rule.PresetFileName);
        }
        AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now, AutoProfileActivityStore.KindApplied,
            app.ProcessName, rule.PresetFileName,
            Str.T("Str.FilterWatcherApplied", app.ProcessName, outcome)));
        // 「进游戏时提示」：只弹托盘气泡，不抢焦点、不打断游戏。
        if (SettingsService.Current.FilterApplyNotify)
        {
            try { TrayService.NotifyComplete(Str.T("Str.FilterApplyNotifyTitle"), Str.T("Str.FilterApplyNotifyBody", app.ProcessName, PresetLabel(rule.PresetFileName))); }
            catch { /* 气泡失败不影响已经写进屏幕的滤镜 */ }
        }
    }

    /// <summary>预设文件名 → 界面上的名字（生成式预设按枚举标题，外部 .icc 用文件名）。</summary>
    private static string PresetLabel(string presetFileName)
    {
        if (string.IsNullOrWhiteSpace(presetFileName)) return Str.T("Str.FilterRuleVibranceOnly");
        if (presetFileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal)
            && Enum.TryParse<IccFilterPreset>(presetFileName[GameFilterLibrary.GeneratedPrefix.Length..], ignoreCase: false, out var preset))
            return ScreenColorSettings.TitleFor(preset);
        return GameFilterCatalog.All.FirstOrDefault(p => p.FileName.Equals(presetFileName, StringComparison.OrdinalIgnoreCase))?.Name
            ?? System.IO.Path.GetFileNameWithoutExtension(presetFileName);
    }

    private void Disengage(string reason, bool suppressed)
    {
        FilterAppRule? previous; string? identity;
        lock (_gate)
        {
            previous = _appliedRule; identity = _appliedDisplay;
            if (!_applied) { _detail = ""; return; }
            _applied = false; _appliedRule = null; _appliedDisplay = null; _appliedPid = 0;
        }
        try
        {
            bool restored;
            if (RestoreOverride is { } hook) restored = hook();
            else { restored = PerAppFilterExecution.Restore(identity!).Length > 0; PerAppFilterExecution.MarkRestored(); }
            lock (_gate) { _lastError = null; _detail = Str.T("Str.FilterWatcherRestored", reason); }
            AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now, AutoProfileActivityStore.KindInfo,
                "", previous?.PresetFileName, Str.T("Str.FilterWatcherRestored", reason) + (restored ? "" : " · " + Str.T("Str.ColorNoBackup"))));
        }
        catch (Exception ex)
        {
            // 恢复失败时重新标为已接管，后续轮询会继续尝试；绝不能丢掉 pending 后遗忘。
            lock (_gate) { _applied = true; _appliedRule = previous; _appliedDisplay = identity; _lastError = ex.Message; _detail = ex.Message; }
            Record(AutoProfileActivityStore.KindFailed, Str.T("Str.FilterWatcherRestoreFailed", PrivacyScrub.Sanitize(ex.Message)));
        }
    }

    public FilterWatcherStatus Status
    {
        get
        {
            lock (_gate)
                return new(SettingsService.Current.FilterWatcherEnabled, _active?.ProcessName,
                    _applied ? _appliedRule?.PresetFileName : null, _detail,
                    FullscreenPresetWatcher.SuppressesForegroundRules, _appliedRule?.Vibrance,
                    _appliedRule?.IncludeScreen == true, _lastError, _paused, _appliedRule?.Auto == true);
        }
    }

    private ForegroundAppInfo? ReadForeground()
        => ForegroundProcessOverride is { } hook ? hook() : _probe.Read();
    private string? ReadDisplay() => DisplayOverride?.Invoke() ?? _probe.DisplayIdentity;
    private void SetDetail(string text) { lock (_gate) _detail = text; }
    private void SetError(string text) { lock (_gate) { _detail = text; _lastError = text; } }
    private static bool IsProcessAlive(int pid)
    {
        if (ProcessAliveOverride is { } hook) return hook(pid);
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; } // 受保护进程读不到退出状态时保持现状，不误还原。
    }

    private static void Record(string kind, string text)
        => AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now, kind, "", null, text));

    public void Dispose()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); _timer.Dispose();
        }
        _state.Reset(); Disengage(Str.T("Str.FilterWatcherReasonExit"), false);
    }
}

internal sealed class Win32PerAppForegroundProbe : IPerAppForegroundProbe
{
    public ForegroundAppInfo? Read()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == nint.Zero) return null;
            _ = GetWindowThreadProcessId(window, out var rawPid);
            var pid = (int)rawPid;
            if (pid <= 0 || pid == Environment.ProcessId) return null;
            using var process = Process.GetProcessById(pid);
            var title = new StringBuilder(512);
            _ = GetWindowText(window, title, title.Capacity);
            string path = "";
            try { path = process.MainModule?.FileName ?? ""; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return new(process.ProcessName + ".exe", path, title.ToString(), pid);
        }
        catch { return null; }
    }

    public string? DisplayIdentity
    {
        get
        {
            try
            {
                var window = GetForegroundWindow();
                var device = "";
                if (window != nint.Zero)
                {
                    var monitor = MonitorFromWindow(window, 2);
                    var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                    if (GetMonitorInfo(monitor, ref info)) device = info.szDevice;
                }
                var displays = DisplayTopology.GetDisplays().Where(d => d.Independent).ToArray();
                var selected = displays.FirstOrDefault(d => d.GdiDeviceName.Equals(device, StringComparison.OrdinalIgnoreCase))
                    ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
                return selected is null ? null : DisplayTopology.Identity(selected);
            }
            catch { return null; }
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize; public int left, top, right, bottom, workLeft, workTop, workRight, workBottom, flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
}
