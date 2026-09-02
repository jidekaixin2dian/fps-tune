using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 按绑定的进程名自动应用 Profile。
/// 轮询只使用 Process.GetProcessesByName，不读取 MainModule、进程路径或进程内存。
/// </summary>
public sealed class AutoProfileService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifetimeGate = new();
    private readonly ProcessEdgeTracker _edgeTracker = new();
    private readonly Dictionary<string, string> _probeErrors = new(StringComparer.OrdinalIgnoreCase);
    private string? _bindingSignature;
    private bool _needsBaseline = true;
    private int _polling;
    private int _disposed;
    private int _activePolls;
    private int _activeApplications;
    private TaskCompletionSource<object?>? _pollsDrained;
    private Task? _disposeTask;

    public AutoProfileService()
    {
        _timer = new Timer(TimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        lock (_lifetimeGate)
        {
            if (_disposed != 0 || _stop.IsCancellationRequested)
                return;
            _timer.Change(TimeSpan.Zero, PollInterval);
        }
    }

    private void TimerTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.Exchange(ref _polling, 1) != 0)
            return;

        lock (_lifetimeGate)
        {
            if (_disposed != 0 || _stop.IsCancellationRequested)
            {
                Volatile.Write(ref _polling, 0);
                return;
            }

            if (_activePolls++ == 0)
                _pollsDrained = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 在生命周期锁内启动 async 方法：Dispose 若先取得锁，绝不会在其后启动新的轮询。
            _ = RunPollAsync();
        }
    }

    private async Task RunPollAsync()
    {
        try
        {
            await PollAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TryAudit(
                new AutoProfileEvent(DateTime.Now, AutoProfileActivityStore.KindFailed, "", null,
                    "轮询异常：" + SafeError(ex)),
                "轮询异常：" + SafeError(ex));
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
            lock (_lifetimeGate)
            {
                if (--_activePolls == 0)
                {
                    _pollsDrained?.TrySetResult(null);
                    _pollsDrained = null;
                }
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = SettingsService.Current;
        if (!settings.AutoProfileEnabled)
        {
            _edgeTracker.Reset();
            _probeErrors.Clear();
            _bindingSignature = null;
            _needsBaseline = true;
            return;
        }

        // 即使绑定被停用也继续观察其进程名：这样停用/重新启用时，正在运行的进程
        // 不会被误判成新的启动边沿。轮询仍然只调用 Process.GetProcessesByName。
        var configured = settings.AutoProfileBindings?
            .Where(x => x is not null)
            .Select(x => x.Clone())
            .Where(x => AutoProfileBinding.NormalizeProcessName(x.ProcessName).Length > 0)
            .GroupBy(x => AutoProfileBinding.NormalizeProcessName(x.ProcessName), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                // 重复绑定不重复轮询/应用；若首条被停用，优先使用同名的启用条目。
                var binding = g.FirstOrDefault(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ProfileName))
                    ?? g.First();
                binding.ProcessName = g.Key;
                return binding;
            })
            .ToList() ?? new List<AutoProfileBinding>();

        if (configured.Count == 0)
        {
            _edgeTracker.Reset();
            _probeErrors.Clear();
            _bindingSignature = null;
            _needsBaseline = true;
            return;
        }

        AutoProfileActivityStore.NoteScan();

        var activeBindings = configured
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ProfileName))
            .ToDictionary(x => x.ProcessName, StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in configured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states.ContainsKey(binding.ProcessName))
                continue;

            var probe = ProbeProcess(binding.ProcessName);
            states[binding.ProcessName] = probe.Running;
            if (probe.Error is { } error)
            {
                if (!_probeErrors.TryGetValue(binding.ProcessName, out var previous)
                    || !string.Equals(previous, error, StringComparison.Ordinal))
                {
                    var profile = activeBindings.TryGetValue(binding.ProcessName, out var active)
                        ? active.ProfileName
                        : null;
                    var detail = $"扫描进程「{binding.ProcessName}」失败：{error}；本轮不会触发启动边沿。";
                    TryAudit(new AutoProfileEvent(
                        DateTime.Now, AutoProfileActivityStore.KindFailed, binding.ProcessName, profile, detail), detail);
                }
                _probeErrors[binding.ProcessName] = error;
            }
            else
            {
                _probeErrors.Remove(binding.ProcessName);
            }
        }

        var signature = string.Join("\u001f", configured
            .Select(x => $"{x.ProcessName}\u001e{x.Enabled}\u001e{x.ProfileName}\u001e{x.ExePath}")
            .OrderBy(x => x, StringComparer.Ordinal));
        var configurationChanged = _needsBaseline
            || !string.Equals(_bindingSignature, signature, StringComparison.Ordinal);
        _bindingSignature = signature;
        _needsBaseline = false;
        if (configurationChanged)
        {
            // 服务首次观察、全局开关恢复、添加/停用/修改绑定都只建立基线；
            // 只有之后观测到 false → true 才算真实启动边沿。
            _edgeTracker.Observe(states);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var processName in _edgeTracker.Update(states))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activeBindings.TryGetValue(processName, out var binding))
            {
                var applyTask = StartBinding(binding, cancellationToken);
                if (applyTask is null)
                    return;
                try
                {
                    await applyTask.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeApplications);
                }
            }
            else
            {
                TryAudit(new AutoProfileEvent(
                    DateTime.Now, AutoProfileActivityStore.KindSkipped, processName, null,
                    "检测到进程启动，但没有对应的有效启用绑定。"));
            }
        }
    }

    private sealed record ProcessProbe(bool? Running, string? Error);

    private static ProcessProbe ProbeProcess(string processName)
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcessesByName(processName);
            return new ProcessProbe(processes.Length > 0, null);
        }
        catch (Exception ex)
        {
            // 查询失败时保留上一轮边沿状态，避免一次权限/瞬时错误重复触发；
            // 同时把失败原因交给活动中心，而不是静默吞掉。
            return new ProcessProbe(null, SafeError(ex));
        }
        finally
        {
            if (processes is not null)
                foreach (var process in processes)
                    process.Dispose();
        }
    }

    private static string SafeError(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : PrivacyScrub.Sanitize(ex.Message);

    internal bool TryAudit(AutoProfileEvent e, string? logMessage = null)
    {
        lock (_lifetimeGate)
        {
            if (_disposed != 0 || _stop.IsCancellationRequested)
                return false;

            AutoProfileActivityStore.Append(e);
            if (!string.IsNullOrWhiteSpace(logMessage))
                Record(logMessage);
            return true;
        }
    }

    private Task? StartBinding(AutoProfileBinding binding, CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            if (_disposed != 0 || cancellationToken.IsCancellationRequested)
                return null;

            // 匹配审计与 ApplyBindingAsync 的启动处于同一生命周期临界区；
            // Dispose 若先取得锁，绝不会在退出后再产生新的应用或审计。
            AutoProfileActivityStore.Append(new AutoProfileEvent(
                DateTime.Now, AutoProfileActivityStore.KindMatch, binding.ProcessName, binding.ProfileName,
                "检测到进程启动，准备自动应用方案。"));
            Interlocked.Increment(ref _activeApplications);
            try
            {
                // async 方法会同步执行到第一个 await，因此底层应用若开始，必在线程释放生命周期锁前开始。
                return ApplyBindingAsync(binding, cancellationToken);
            }
            catch
            {
                Interlocked.Decrement(ref _activeApplications);
                throw;
            }
        }
    }

    private async Task ApplyBindingAsync(
        AutoProfileBinding binding, CancellationToken cancellationToken)
    {
        void Notify(string kind, string title, string message)
        {
            if (!TryAudit(
                    new AutoProfileEvent(DateTime.Now, kind, binding.ProcessName, binding.ProfileName, message),
                    title + "：" + message))
                return;

            try
            {
                TrayService.NotifyComplete("FPS 帧律 · " + title, message);
            }
            catch (Exception ex)
            {
                // The tray path is best-effort and must not keep the lifetime
                // gate held while it marshals to the UI dispatcher.
                TryAudit(new AutoProfileEvent(
                    DateTime.Now, AutoProfileActivityStore.KindFailed, binding.ProcessName, binding.ProfileName,
                    "通知失败：" + SafeError(ex)), "通知失败：" + SafeError(ex));
            }
        }

        try
        {
            if (!ProfileStore.TryLoad(out var profiles, out var loadError))
            {
                Notify(AutoProfileActivityStore.KindFailed, "自动应用失败", $"方案文件读取失败：{loadError}");
                return;
            }

            var profile = profiles.FirstOrDefault(x => x is not null
                && string.Equals(x.Name, binding.ProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                Notify(AutoProfileActivityStore.KindSkipped, "自动应用跳过",
                    $"未找到方案「{binding.ProfileName}」，未修改系统设置。");
                return;
            }

            var catalog = ItemCatalog.All.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var rawIds = (profile.Ids ?? Array.Empty<string>()).ToArray();
            var invalidIds = rawIds
                .Where(id => string.IsNullOrWhiteSpace(id) || !catalog.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (invalidIds.Length > 0)
            {
                Notify(AutoProfileActivityStore.KindSkipped, "自动应用跳过",
                    $"方案「{profile.Name}」包含未知或无效优化项（{string.Join("、", invalidIds.Where(x => !string.IsNullOrWhiteSpace(x)).DefaultIfEmpty("空 id"))}），整份方案已跳过。");
                return;
            }

            var ids = rawIds
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0)
            {
                Notify(AutoProfileActivityStore.KindSkipped, "自动应用跳过",
                    $"方案「{profile.Name}」为空或不包含有效优化项，未修改系统设置。");
                return;
            }

            if (ids.Any(id => catalog[id].Admin) && !AdminHelper.IsAdministrator())
            {
                Notify(AutoProfileActivityStore.KindSkipped, "自动应用跳过",
                    $"方案「{profile.Name}」包含需要管理员权限的项目；当前不是管理员，整份方案已跳过。");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await OptimizationEngine.ApplyItemsAsync(ids, binding.ExePath)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                var error = string.IsNullOrWhiteSpace(result.Error) ? "未提供错误信息" : result.Error.Trim();
                Notify(AutoProfileActivityStore.KindFailed, "自动应用失败",
                    $"方案「{profile.Name}」执行失败：{error}");
                return;
            }

            var incomplete = ReadIncompleteItems(result.Output);
            if (incomplete.Count > 0)
            {
                Notify(AutoProfileActivityStore.KindSkipped, "自动应用未完成",
                    $"方案「{profile.Name}」有 {incomplete.Count} 项未完成：{string.Join("、", incomplete)}");
                return;
            }

            Notify(AutoProfileActivityStore.KindApplied, "自动应用完成",
                $"检测到「{binding.DisplayName}」启动，已应用方案「{profile.Name}」（{ids.Length} 项）。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Notify(AutoProfileActivityStore.KindFailed, "自动应用失败",
                $"方案「{binding.ProfileName}」执行异常：{SafeError(ex)}");
        }
    }

    private static IReadOnlyList<string> ReadIncompleteItems(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return new[] { "结果无法解析" };

            var bad = new List<string>();
            foreach (var item in results.EnumerateArray())
            {
                var ok = item.TryGetProperty("ok", out var okValue) && okValue.GetBoolean();
                var skipped = item.TryGetProperty("skipped", out var skippedValue) && skippedValue.GetBoolean();
                if (!ok || skipped)
                {
                    var id = item.TryGetProperty("id", out var idValue)
                        ? idValue.GetString()
                        : null;
                    bad.Add(string.IsNullOrWhiteSpace(id) ? "未知项目" : id);
                }
            }
            return bad;
        }
        catch
        {
            return new[] { "结果无法解析" };
        }
    }

    private static void Record(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FpsTune", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "auto-profile.log");
            message = PrivacyScrub.Sanitize(message);
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            Trace.WriteLine(message);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task drain;
        lock (_lifetimeGate)
        {
            if (_disposed == 0)
            {
                _disposed = 1;
                _stop.Cancel();
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer.Dispose();
                _edgeTracker.Reset();
                _probeErrors.Clear();
            }

            drain = _pollsDrained?.Task ?? Task.CompletedTask;
            if (Volatile.Read(ref _activeApplications) > 0)
            {
                Trace.WriteLine(
                    "AutoProfileService 正在退出：底层 ApplyItemsAsync 不支持取消，等待其完成且不再发送通知/审计。");
            }
        }

        // 等待当前轮询（包括不可取消的底层应用）结束，避免后台任务越过服务生命周期。
        await drain.ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>纯逻辑的启动边沿状态机：只在 false → true 时返回一次。</summary>
internal sealed class ProcessEdgeTracker
{
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baselinePending = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Update(IReadOnlyDictionary<string, bool?> states)
    {
        var names = states.Keys
            .Select(AutoProfileBinding.NormalizeProcessName)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _running.RemoveWhere(x => !names.Contains(x));
        _baselinePending.RemoveWhere(x => !names.Contains(x));

        var started = new List<string>();
        foreach (var pair in states)
        {
            var name = AutoProfileBinding.NormalizeProcessName(pair.Key);
            if (name.Length == 0 || !pair.Value.HasValue)
                continue;
            if (pair.Value.Value)
            {
                if (_running.Add(name))
                {
                    if (_baselinePending.Remove(name))
                        continue;
                    started.Add(name);
                }
                else
                {
                    // Observe() 可能在进程已运行时建立了 true 基线。
                    _baselinePending.Remove(name);
                }
            }
            else
            {
                _running.Remove(name);
                _baselinePending.Remove(name);
            }
        }
        return started;
    }

    /// <summary>记录当前状态但不把当前已运行进程当作启动边沿。</summary>
    public void Observe(IReadOnlyDictionary<string, bool?> states)
    {
        var names = states.Keys
            .Select(AutoProfileBinding.NormalizeProcessName)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _running.RemoveWhere(x => !names.Contains(x));
        _baselinePending.RemoveWhere(x => !names.Contains(x));

        foreach (var pair in states)
        {
            var name = AutoProfileBinding.NormalizeProcessName(pair.Key);
            if (name.Length == 0)
                continue;

            _baselinePending.Add(name);
            if (pair.Value == true)
                _running.Add(name);
            else if (pair.Value == false)
                _running.Remove(name);
        }
    }

    public void Reset()
    {
        _running.Clear();
        _baselinePending.Clear();
    }
}
