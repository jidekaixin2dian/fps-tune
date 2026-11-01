using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>独立程序滤镜守护状态。</summary>
public sealed record FilterWatcherStatus(bool Enabled, string? ActiveProcess, string? ActivePreset);

/// <summary>
/// 独立程序滤镜守护（opt-in，默认关闭）：前台窗口的进程命中规则时应用对应滤镜 ICC，
/// 离开命中进程或关闭开关即还原原 ICC。3 秒轮询；只在状态变化时写系统，
/// 与 IccFilterService 的备份/读回/漂移保护共用同一契约。
/// </summary>
internal sealed class ForegroundFilterWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private bool _applied;
    private string? _appliedPreset;
    private string? _appliedDisplay;
    private string? _activeProcess;
    private int _polling;
    private int _disposed;

    internal static Func<string?>? ForegroundProcessOverride { get; set; }
    internal static Func<string?, bool>? ApplyOverride { get; set; }
    internal static Func<bool>? RestoreOverride { get; set; }
    internal static Func<string?>? DisplayOverride { get; set; }

    public ForegroundFilterWatcher()
        => _timer = new Timer(TimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _timer.Change(TimeSpan.Zero, PollInterval);
        }
    }

    /// <summary>开关或规则变化后立即重估。</summary>
    internal void ApplySettings() => Poll();

    private void TimerTick(object? state) => Poll();

    internal void Poll()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            if (!SettingsService.Current.FilterWatcherEnabled)
            {
                Disengage(Str.T("Str.FilterWatcherReasonOff"));
                return;
            }
            // 互斥（用户拍板，不做优先级链）：全屏方案守护开启时由它接管显示写入，
            // 进程规则先还原再暂停，避免两套状态机互相 Restore 撕裂注册表。
            if (FullscreenPresetWatcher.SuppressesForegroundRules)
            {
                Disengage(Str.T("Str.FullscreenReasonTakeover"));
                return;
            }
            var foreground = (ForegroundProcessOverride ?? ReadForegroundProcess)();
            var rule = foreground is null ? null : IccAppRuleStore.Match(foreground);
            lock (_gate)
            {
                _activeProcess = foreground;
            }
            if (rule is null)
            {
                Disengage(Str.T("Str.FilterWatcherReasonLeave"));
                return;
            }
            var display = (DisplayOverride ?? ReadPrimaryDisplay)();
            lock (_gate)
            {
                if (_applied && _appliedPreset == rule.PresetFileName && _appliedDisplay == display) return;
            }
            // 同一预设已应用则跳过；否则切到规则预设。
            var presetFile = ResolvePresetPath(rule.PresetFileName);
            var applied = ApplyOverride is { } hook
                ? hook(presetFile)
                : TryApply(presetFile, display);
            if (applied)
            {
                lock (_gate)
                {
                    _applied = true;
                    _appliedPreset = rule.PresetFileName;
                    _appliedDisplay = display;
                }
                AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now,
                    AutoProfileActivityStore.KindInfo, foreground ?? "", rule.PresetFileName,
                    Str.T("Str.FilterWatcherApplied", foreground ?? "", rule.PresetFileName)));
            }
        }
        catch (Exception ex)
        {
            AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now,
                AutoProfileActivityStore.KindInfo, "", null,
                Str.T("Str.FilterWatcherFailed", PrivacyScrub.Sanitize(ex.Message))));
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void Disengage(string reason)
    {
        string? preset;
        lock (_gate)
        {
            if (!_applied) return;
            preset = _appliedPreset;
            _applied = false;
            _appliedPreset = null;
            _appliedDisplay = null;
        }
        var restored = RestoreOverride is { } hook ? hook() : TryRestore();
        AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now,
            AutoProfileActivityStore.KindInfo, "", preset ?? "",
            restored ? Str.T("Str.FilterWatcherRestored", reason)
                     : Str.T("Str.FilterWatcherRestoreFailed", reason)));
    }

    public FilterWatcherStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new(SettingsService.Current.FilterWatcherEnabled, _activeProcess,
                    _applied ? _appliedPreset : null);
            }
        }
    }

    private static bool TryApply(string presetPath, string? display)
    {
        try { IccFilterService.ApplyFromFile(presetPath, display); return true; }
        catch { return false; }
    }

    private static bool TryRestore()
    {
        try { return IccFilterService.Restore(); }
        catch { return false; }
    }

    private static string ResolvePresetPath(string fileName)
    {
        // 内置预设解包到临时文件（IccFilterService 从文件安装）。
        var bytes = GameFilterLibrary.ReadPreset(fileName);
        var temp = Path.Combine(Path.GetTempPath(), "FpsTune-filter-" + Guid.NewGuid().ToString("N") + ".icc");
        File.WriteAllBytes(temp, bytes);
        return temp;
    }

    private static string? ReadForegroundProcess()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == nint.Zero) return null;
            _ = GetWindowThreadProcessId(handle, out var pidBuf);
            var pid = (int)pidBuf;
            if (pid <= 0 || pid == Environment.ProcessId) return null;
            using var process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch { return null; }
    }

    private static string? ReadPrimaryDisplay()
    {
        try
        {
            var displays = DisplayTopology.GetDisplays().Where(d => d.Independent).ToArray();
            return displays.FirstOrDefault(d => d.IsPrimary) is { } primary
                ? DisplayTopology.Identity(primary)
                : displays.FirstOrDefault() is { } first ? DisplayTopology.Identity(first) : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Dispose();
        }
        Disengage(Str.T("Str.FilterWatcherReasonExit"));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
}
