using System.IO;
using System.Threading;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>全屏方案守护状态（界面只读展示）。DisplayName 是友好名，不暴露显示器设备身份。</summary>
public sealed record FullscreenPresetStatus(bool Enabled, string? ProcessName, string? ActivePreset, string? DisplayName);

/// <summary>
/// 全屏自动切换画面方案（opt-in，默认关闭）。
///
/// 语义：当前台窗口真正进入全屏（无边框或独占），把选定方案套到**窗口所在那块屏**；
/// 退出全屏即还原。与 <see cref="ForegroundFilterWatcher"/> 互斥——两个都开时本守护生效，
/// 进程规则暂停（见 <see cref="SuppressesForegroundRules"/>）。
///
/// 线程：单个 500 ms Timer + Interlocked 防重入；系统写入全部在 Timer 线程，
/// UI 通知由调用方自行切 Dispatcher。所有写入复用 IccFilterService / TransientGamma
/// 的备份、读回验证与 SystemMutationGate 跨进程互斥。
/// </summary>
internal sealed class FullscreenPresetWatcher : IDisposable
{
    internal const string ChannelGamma = "gamma";
    internal const string ChannelIcc = "icc";

    /// <summary>采样间隔。判定靠去抖兜，不靠拉长间隔。</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>测试注入：替换真实 Win32 探针。</summary>
    internal static Func<IForegroundProbe>? ProbeFactory;

    private readonly FullscreenDetector _detector;
    private readonly FullscreenDebouncer _debouncer;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();

    private bool _applied;
    private FullscreenPending? _pending;
    private string? _appliedIdentity;
    private IccFilterPreset? _appliedPreset;
    private string? _appliedChannel;
    private string? _activeProcess;
    private string? _lastIdentity;
    private int _polling;
    private int _disposed;

    internal FullscreenPresetWatcher(IForegroundProbe? probe = null, FullscreenDebouncer? debouncer = null)
    {
        _detector = new FullscreenDetector(probe ?? ProbeFactory?.Invoke() ?? new Win32ForegroundProbe());
        _debouncer = debouncer ?? new FullscreenDebouncer();
        _timer = new System.Threading.Timer(TimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>两个守护都开时以全屏方案为准，进程规则暂停（拍板结论，不做优先级链）。</summary>
    internal static bool SuppressesForegroundRules => SettingsService.Current.FullscreenPresetEnabled;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
        }
        // 上次异常退出可能把某一屏留在方案状态：先还原，再开始轮询。
        RestoreOnStartup();
        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    /// <summary>设置或方案变化后立即重估，并清掉旧样本。</summary>
    internal void ApplySettings()
    {
        _debouncer.Reset();
        Poll();
    }

    private void TimerTick(object? state) => Poll();

    internal FullscreenPresetStatus Status
    {
        get
        {
            string? process, presetLabel, identity;
            lock (_gate)
            {
                process = _activeProcess;
                presetLabel = _applied && _appliedPreset is { } preset ? PresetLabel(preset) : null;
                identity = _appliedIdentity;
            }
            // 友好名解析要枚举显示拓扑，放在锁外，别让界面读状态时卡住轮询线程。
            return new(SettingsService.Current.FullscreenPresetEnabled, process, presetLabel,
                identity is null ? null : DisplayLabel(identity));
        }
    }

    internal void Poll()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            var settings = SettingsService.Current;
            if (!settings.FullscreenPresetEnabled)
            {
                _debouncer.Reset();
                Disengage(Str.T("Str.FullscreenReasonOff"));
                return;
            }

            if (ParsePreset(settings.FullscreenPresetName) is not { } preset)
            {
                Disengage(Str.T("Str.FullscreenReasonNoPreset"));
                return;
            }

            var state = _detector.Evaluate(out var identity, out var processName);
            lock (_gate)
            {
                _activeProcess = processName;
                if (identity is not null) _lastIdentity = identity;
            }

            // 只对已知游戏生效：非游戏全屏（视频、浏览器）按「不是全屏」处理，
            // 这样已生效的方案会被还原，符合用户预期。
            if (settings.FullscreenPresetOnlyForGames
                && !GamePathService.IsKnownGameExecutable(processName ?? string.Empty))
                state = ForegroundScreenState.NotFullscreen;

            if (!_debouncer.Update(state, DateTime.UtcNow)) return;

            if (_debouncer.Committed) Engage(settings, preset, identity);
            else Disengage(Str.T("Str.FullscreenReasonLeave"));
        }
        catch (Exception ex)
        {
            Record(AutoProfileActivityStore.KindFailed,
                Str.T("Str.FullscreenFailed", PrivacyScrub.Sanitize(ex.Message)));
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void Engage(AppSettings settings, IccFilterPreset preset, string? identity)
    {
        var channel = NormalizeChannel(settings.FullscreenPresetChannel);
        var target = identity ?? _lastIdentity;
        if (target is null)
        {
            Record(AutoProfileActivityStore.KindFailed, Str.T("Str.FullscreenFailed", Str.T("Str.DisplayDisconnected")));
            return;
        }

        lock (_gate)
        {
            if (_applied && _appliedIdentity == target && _appliedPreset == preset && _appliedChannel == channel)
                return;
        }
        // 换屏 / 换方案 / 换通道：先把上一次的改动还原干净再重来。
        Disengage(Str.T("Str.FullscreenReasonRetarget"));

        FullscreenPending pending;
        if (channel == ChannelGamma)
        {
            pending = TransientGamma.Apply(target, preset);
        }
        else
        {
            var path = MaterializePreset(preset);
            try { IccFilterService.ApplyFromFile(path, target, startNewBaselineOnDrift: false); }
            finally { TryDelete(path); }
            pending = new FullscreenPending(FullscreenPendingStore.CurrentVersion, target, ChannelIcc, preset.ToString(), DateTime.Now);
        }

        // 先落盘再对外宣称已生效：崩溃时留下的凭证就是还原依据。
        FullscreenPendingStore.Save(pending);
        lock (_gate)
        {
            _applied = true;
            _pending = pending;
            _appliedIdentity = target;
            _appliedPreset = preset;
            _appliedChannel = channel;
        }
        Record(AutoProfileActivityStore.KindApplied,
            Str.T("Str.FullscreenApplied", PresetLabel(preset), DisplayLabel(target)));
    }

    private void Disengage(string reason)
    {
        FullscreenPending? pending;
        lock (_gate)
        {
            if (!_applied) return;
            pending = _pending;
            _applied = false;
            _pending = null;
            _appliedIdentity = null;
            _appliedPreset = null;
            _appliedChannel = null;
        }
        if (pending is null)
        {
            FullscreenPendingStore.Delete();
            return;
        }

        try
        {
            var restored = pending.Channel == ChannelGamma
                ? TransientGamma.Restore(pending)
                : IccFilterService.Restore(pending.Identity);
            // 调用没抛异常 = 已还原或本来就没有可还原的改动，凭证可以清掉；
            // 抛异常（漂移/显示器变化/HDR）时保留凭证，交给下次启动或用户手动处理。
            FullscreenPendingStore.Delete();
            Record(AutoProfileActivityStore.KindInfo, restored
                ? Str.T("Str.FullscreenRestored", reason)
                : Str.T("Str.FullscreenRestoreNothing", reason));
        }
        catch (Exception ex)
        {
            Record(AutoProfileActivityStore.KindFailed,
                Str.T("Str.FullscreenRestoreFailed", reason + " " + PrivacyScrub.Sanitize(ex.Message)));
        }
    }

    /// <summary>启动兜底：凭证存在就说明上次没还原干净。</summary>
    internal static bool RestoreOnStartup()
    {
        var pending = FullscreenPendingStore.Load();
        if (pending is null) return false;
        try
        {
            if (pending.Channel == ChannelGamma) _ = TransientGamma.Restore(pending);
            else _ = IccFilterService.Restore(pending.Identity);
            FullscreenPendingStore.Delete();
            Record(AutoProfileActivityStore.KindInfo, Str.T("Str.FullscreenStartupRestored"));
            return true;
        }
        catch (Exception ex)
        {
            Record(AutoProfileActivityStore.KindFailed,
                Str.T("Str.FullscreenStartupRestoreFailed", PrivacyScrub.Sanitize(ex.Message)));
            return false;
        }
    }

    internal static string NormalizeChannel(string? channel)
        => string.Equals(channel, ChannelIcc, StringComparison.OrdinalIgnoreCase) ? ChannelIcc : ChannelGamma;

    internal static IccFilterPreset? ParsePreset(string? name)
        => Enum.TryParse<IccFilterPreset>(name ?? string.Empty, ignoreCase: false, out var preset) ? preset : null;

    internal static string PresetLabel(IccFilterPreset preset) => ScreenColorSettings.TitleFor(preset);

    private static string DisplayLabel(string identity)
    {
        try
        {
            var display = DisplayTopology.Require(identity);
            return string.IsNullOrWhiteSpace(display.FriendlyName) ? display.GdiDeviceName : display.FriendlyName;
        }
        catch
        {
            return identity;
        }
    }

    /// <summary>内置方案即时生成 .icc（IccFilterService 从文件安装）。</summary>
    private static string MaterializePreset(IccFilterPreset preset)
    {
        var bytes = GameFilterLibrary.ReadPreset(GameFilterLibrary.GeneratedPrefix + preset);
        var path = Path.Combine(Path.GetTempPath(), "FpsTune-fullscreen-" + Guid.NewGuid().ToString("N") + ".icc");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void Record(string kind, string detail)
        => AutoProfileActivityStore.Append(new AutoProfileEvent(DateTime.Now, kind, "", null, detail));

    public void Dispose()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Dispose();
        }
        Disengage(Str.T("Str.FullscreenReasonExit"));
    }
}
