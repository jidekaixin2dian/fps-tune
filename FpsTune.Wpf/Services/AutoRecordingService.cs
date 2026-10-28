using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record GameProcessRun(int Pid, long Started);
internal sealed record GameProcessObservation(GameProcessRun? Run, bool Reliable = true);

/// <summary>Opt-in launch/exit recording. Never stops a session it did not start.</summary>
internal sealed class AutoRecordingService : IDisposable
{
    private readonly PerformanceSessionService _session;
    private readonly Func<(bool Enabled, IReadOnlyList<string> Paths)> _options;
    private readonly Func<string, GameProcessObservation> _probe;
    private readonly Func<bool> _busy;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, GameProcessRun?> _observed = new(StringComparer.OrdinalIgnoreCase);
    private (Guid Id, string Path, GameProcessRun Run)? _owned;
    private string _signature = "";
    private int _generation;
    private bool _polling, _refreshPending, _disposed, _uncertain;
    internal event Action? Changed;
    internal string Status { get; private set; } = Str.T("Str.AutoRecordOff");
    internal bool IsObserving => _timer.IsEnabled;

    internal AutoRecordingService(PerformanceSessionService session,
        Func<(bool Enabled, IReadOnlyList<string> Paths)>? options = null,
        Func<string, GameProcessObservation>? probe = null, Func<bool>? busy = null)
    {
        _session = session;
        _options = options ?? (() => (SettingsService.Current.AutoRecordEnabled, SettingsService.Current.AutoRecordGames));
        _probe = probe ?? ObserveProcess;
        _busy = busy ?? (() => ExperimentRunner.IsRunning || GamePreparationService.IsRunning);
        _timer.Tick += async (_, _) => await PollAsync();
    }
    internal static string[] NormalizePaths(IEnumerable<string>? paths) => (paths ?? []).Select(SafePath).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string? SafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl)
            || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Path.GetFullPath(path); } catch (ArgumentException) { return null; } catch (NotSupportedException) { return null; }
    }

    internal void ApplySettings()
    {
        if (_disposed) return;
        _generation++; _refreshPending = true;
        var enabled = _options().Enabled;
        if (enabled || _owned is not null) _timer.Start(); else _timer.Stop();
        if (!_polling) _ = PollAsync();
    }
    internal async Task PollAsync()
    {
        if (_disposed || _polling) return;
        _polling = true; _refreshPending = false;
        var generation = _generation;
        try
        {
            var options = _options(); var paths = NormalizePaths(options.Paths);
            var signature = options.Enabled + "|" + string.Join("|", paths.Select(p => p.ToUpperInvariant()));
            if (_owned is { } owned && (!_session.IsRunning || _session.RunId != owned.Id))
            { _owned = null; SetStatus(Str.T("Str.AutoRecordWaiting")); }
            if (_signature != signature)
            {
                if (_owned is not null) await SaveOwnedAsync();
                if (_disposed || generation != _generation) return;
                _signature = signature; _observed.Clear();
                _uncertain = false;
                SetStatus(Str.T(options.Enabled ? "Str.AutoRecordWaiting" : "Str.AutoRecordOff"));
            }
            if (!options.Enabled || paths.Length == 0)
            {
                SetStatus(Str.T(options.Enabled ? "Str.AutoRecordSelectGames" : "Str.AutoRecordOff"));
                _timer.Stop(); return;
            }
            var observations = await Task.Run(() => paths.ToDictionary(p => p, _probe, StringComparer.OrdinalIgnoreCase));
            if (_disposed || generation != _generation) return;
            if (_owned is { } active && observations.TryGetValue(active.Path, out var current)
                && current.Reliable && current.Run != active.Run)
                await SaveOwnedAsync();
            if (_disposed || generation != _generation) return;
            var uncertain = observations.Values.Any(o => !o.Reliable);
            if (uncertain) SetStatus(Str.T("Str.AutoRecordUnknown"));
            else if (_uncertain) SetStatus(_owned is { } monitoring ? Str.T("Str.AutoRecordRunning", GamePathService.LabelFor(monitoring.Path)) : Str.T("Str.AutoRecordWaiting"));
            _uncertain = uncertain;
            foreach (var path in paths)
            {
                var observation = observations[path];
                if (!observation.Reliable) continue; // Unknown is not an exit or a new launch.
                var known = _observed.TryGetValue(path, out var before);
                _observed[path] = observation.Run;
                if (!known || observation.Run is null || observation.Run == before) continue;
                if (_session.IsRunning || _session.IsStopping || _busy())
                { SetStatus(Str.T("Str.AutoRecordBusy")); continue; }
                var name = Path.GetFileNameWithoutExtension(path);
                name = name[..Math.Min(name.Length, 30)];
                if (_session.StartForGame($"{name} · {Str.T("Str.AutoRecordName")} {DateTime.Now:MM-dd HH-mm}", path, observation.Run))
                {
                    _owned = (_session.RunId, path, observation.Run);
                    SetStatus(Str.T("Str.AutoRecordRunning", GamePathService.LabelFor(path)));
                }
                else SetStatus(Str.T("Str.AutoRecordRejected"));
            }
        }
        catch (Exception ex)
        {
            SetStatus(Str.T("Str.AutoRecordFailed", PrivacyScrub.Sanitize(ex.Message)));
            if (!_options().Enabled && _owned is null) _timer.Stop();
        }
        finally
        {
            _polling = false;
            if (_refreshPending && !_disposed) _ = PollAsync();
        }
    }
    private async Task SaveOwnedAsync()
    {
        if (_owned is not { } owned) return;
        _owned = null;
        if (!_session.IsRunning || _session.RunId != owned.Id) return;
        var saved = await _session.StopAsync(save: true);
        SetStatus(saved is null ? Str.T("Str.AutoRecordNoSave") : Str.T("Str.AutoRecordSaved", saved.Name));
    }
    private void SetStatus(string text) { Status = text; Changed?.Invoke(); }
    public void Dispose() { _disposed = true; _generation++; _timer.Stop(); }

    internal static GameProcessObservation ObserveProcess(string path)
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
            var uncertain = false;
            var matches = new List<GameProcessRun>();
            using var own = Process.GetCurrentProcess();
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != own.SessionId) continue;
                    var image = ProcessIdentity.TryGetImagePath(process);
                    if (image is null) { if (!process.HasExited) uncertain = true; continue; }
                    if (image.Equals(path, StringComparison.OrdinalIgnoreCase))
                        matches.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks));
                }
                catch { uncertain = true; }
            }
            return matches.Count > 0 ? new(matches.OrderBy(r => r.Started).ThenBy(r => r.Pid).First()) : new(null, !uncertain);
        }
        catch { return new(null, false); }
        finally { if (processes is not null) foreach (var process in processes) process.Dispose(); }
    }
}
