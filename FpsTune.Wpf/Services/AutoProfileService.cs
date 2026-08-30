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
public sealed class AutoProfileService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private readonly ProcessEdgeTracker _edgeTracker = new();
    private int _polling;
    private int _disposed;

    public AutoProfileService()
    {
        _timer = new Timer(TimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    private async void TimerTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.Exchange(ref _polling, 1) != 0)
            return;

        try
        {
            await PollAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Record("轮询异常：" + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var settings = SettingsService.Current;
        if (!settings.AutoProfileEnabled)
        {
            _edgeTracker.Reset();
            return;
        }

        var bindings = settings.AutoProfileBindings?
            .Where(x => x is not null && x.Enabled)
            .Select(x => x.Clone())
            .Where(x => !string.IsNullOrWhiteSpace(x.ProcessName)
                        && !string.IsNullOrWhiteSpace(x.ProfileName))
            .GroupBy(x => AutoProfileBinding.NormalizeProcessName(x.ProcessName), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                var binding = g.First();
                binding.ProcessName = g.Key;
                return binding;
            })
            .ToList() ?? new List<AutoProfileBinding>();

        if (bindings.Count == 0)
        {
            _edgeTracker.Reset();
            return;
        }

        var states = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states.ContainsKey(binding.ProcessName))
                continue;
            states[binding.ProcessName] = ProbeProcess(binding.ProcessName);
        }

        foreach (var processName in _edgeTracker.Update(states))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = bindings.FirstOrDefault(x =>
                string.Equals(x.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (binding is not null)
                await ApplyBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool? ProbeProcess(string processName)
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcessesByName(processName);
            return processes.Length > 0;
        }
        catch
        {
            // 查询失败时保留上一轮边沿状态，避免一次权限/瞬时错误重复触发。
            return null;
        }
        finally
        {
            if (processes is not null)
                foreach (var process in processes)
                    process.Dispose();
        }
    }

    private static async Task ApplyBindingAsync(
        AutoProfileBinding binding, CancellationToken cancellationToken)
    {
        try
        {
            if (!ProfileStore.TryLoad(out var profiles, out var loadError))
            {
                Report("自动应用失败", $"方案文件读取失败：{loadError}");
                return;
            }

            var profile = profiles.FirstOrDefault(x =>
                string.Equals(x.Name, binding.ProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                Report("自动应用跳过", $"未找到方案「{binding.ProfileName}」，未修改系统设置。");
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
                Report("自动应用跳过",
                    $"方案「{profile.Name}」包含未知或无效优化项（{string.Join("、", invalidIds.Where(x => !string.IsNullOrWhiteSpace(x)).DefaultIfEmpty("空 id"))}），整份方案已跳过。");
                return;
            }

            var ids = rawIds
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0)
            {
                Report("自动应用跳过", $"方案「{profile.Name}」为空或不包含有效优化项，未修改系统设置。");
                return;
            }

            if (ids.Any(id => catalog[id].Admin) && !AdminHelper.IsAdministrator())
            {
                Report("自动应用跳过", $"方案「{profile.Name}」包含需要管理员权限的项目；当前不是管理员，整份方案已跳过。");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await OptimizationEngine.ApplyItemsAsync(ids, binding.ExePath)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                Report("自动应用失败", $"方案「{profile.Name}」执行失败：{result.Error.Trim()}");
                return;
            }

            var incomplete = ReadIncompleteItems(result.Output);
            if (incomplete.Count > 0)
            {
                Report("自动应用未完成",
                    $"方案「{profile.Name}」有 {incomplete.Count} 项未完成：{string.Join("、", incomplete)}");
                return;
            }

            Report("自动应用完成", $"检测到「{binding.DisplayName}」启动，已应用方案「{profile.Name}」（{ids.Length} 项）。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Report("自动应用失败", $"方案「{binding.ProfileName}」执行异常：{ex.Message}");
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

    private static void Report(string title, string message)
    {
        Record(title + "：" + message);
        try
        {
            TrayService.NotifyComplete("FPS 帧律 · " + title, message);
        }
        catch (Exception ex)
        {
            Record("通知失败：" + ex.Message);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Dispose();
        _stop.Dispose();
        _edgeTracker.Reset();
    }
}

/// <summary>纯逻辑的启动边沿状态机：只在 false → true 时返回一次。</summary>
internal sealed class ProcessEdgeTracker
{
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Update(IReadOnlyDictionary<string, bool?> states)
    {
        var names = states.Keys
            .Select(AutoProfileBinding.NormalizeProcessName)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _running.RemoveWhere(x => !names.Contains(x));

        var started = new List<string>();
        foreach (var pair in states)
        {
            var name = AutoProfileBinding.NormalizeProcessName(pair.Key);
            if (name.Length == 0 || !pair.Value.HasValue)
                continue;
            if (pair.Value.Value)
            {
                if (_running.Add(name))
                    started.Add(name);
            }
            else
            {
                _running.Remove(name);
            }
        }
        return started;
    }

    public void Reset() => _running.Clear();
}
