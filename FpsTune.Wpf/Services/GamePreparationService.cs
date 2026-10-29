using System.Text.Json.Serialization;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

public sealed record GamePreparationOptions
{
    public bool Configured { get; init; }
    public bool CleanMemory { get; init; } = true;
    public bool AllStandby { get; init; }
    public bool FullMemoryCleanup { get; init; }
    public bool ApplyIcc { get; init; }
    public IccFilterPreset? IccPreset { get; init; } = IccFilterPreset.Vivid;
    public bool ApplyVibrance { get; init; }
    public int VibrancePercent { get; init; } = 60;
    public string? DisplayIdentity { get; init; }
    [JsonIgnore] public bool HasActions => CleanMemory || ApplyIcc || ApplyVibrance;
}

internal sealed record PreparationStep(string Name, bool Succeeded, string Detail, string? Error = null);

/// <summary>Composes the existing writers; their original backup and readback contracts remain authoritative.</summary>
internal sealed class GamePreparationService(MemoryCleanupService? memory = null, BackgroundMemoryService? background = null)
{
    private readonly MemoryCleanupService _memory = memory ?? new();
    private readonly BackgroundMemoryService _background = background ?? new();
    [ThreadStatic] private static bool _ownsMutationThread;
    private static int _running;
    internal static bool IsRunning => Volatile.Read(ref _running) != 0;
    // Only the synchronous worker holding the mutation lease may reuse memory cleanup.
    internal static bool BlocksMemoryCleanup => IsRunning && !_ownsMutationThread;
    internal bool IsBusy => _memory.IsBusy || IsRunning;
    internal bool NeedsAdministrator(GamePreparationOptions options) => options.CleanMemory && !_memory.IsAdministrator;

    internal async Task<IReadOnlyList<PreparationStep>> RunAsync(GamePreparationOptions options, IProgress<PreparationStep>? progress = null,
        IProgress<MemoryCleanupProgress>? memoryProgress = null, CancellationToken cancellation = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException(Str.T("Str.PrepareBusy"));
        try
        {
            return await Task.Run(() =>
            {
                using var gate = SystemMutationGate.Acquire();
                _ownsMutationThread = true;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (_memory.IsBusy) throw new InvalidOperationException(Str.T("Str.MemoryBusy"));
                    if (!options.HasActions || options.ApplyIcc && options.IccPreset is { } preset && !Enum.IsDefined(preset)
                        || options.ApplyVibrance && options.VibrancePercent is < 0 or > 100)
                        throw new InvalidOperationException(Str.T("Str.PrepareChoose"));
                    if (NeedsAdministrator(options)) throw new UnauthorizedAccessException(Str.T("Str.MemoryAdmin"));
                    IccDisplayInfo? display = null;
                    if (options.ApplyIcc || options.ApplyVibrance)
                    {
                        if (string.IsNullOrWhiteSpace(options.DisplayIdentity)) throw new InvalidOperationException(Str.T("Str.DisplayDisconnected"));
                        display = DisplayTopology.Require(options.DisplayIdentity);
                        if (options.ApplyIcc && options.IccPreset is not null)
                        {
                            var state = ScreenColorService.GetState(options.DisplayIdentity);
                            if (!state.Supported) throw new InvalidOperationException(state.Detail);
                        }
                        if (options.ApplyVibrance)
                        {
                            var state = DigitalVibranceService.GetState(options.DisplayIdentity);
                            if (!state.Supported) throw new InvalidOperationException(state.UnsupportedReason ?? Str.T("Str.PrepareDisplayUnavailable"));
                        }
                    }
                    var steps = new List<PreparationStep>();
                    void Run(string name, Func<PreparationStep> action)
                    {
                        PreparationStep result;
                        try
                        {
                            if (_memory.IsBusy) throw new InvalidOperationException(Str.T("Str.MemoryBusy"));
                            cancellation.ThrowIfCancellationRequested();
                            result = action();
                        }
                        catch (Exception ex) { result = new(name, false, PrivacyScrub.Sanitize(ex.Message), PrivacyScrub.Sanitize(ex.Message)); }
                        steps.Add(result); progress?.Report(result);
                    }
                    if (options.ApplyIcc) Run(Str.T("Str.PrepareIcc"), () =>
                    {
                        DisplayTopology.ValidateConnection(display!);
                        string result;
                        if (options.IccPreset is { } value)
                        {
                            ScreenColorService.Apply(ScreenColorSettings.ForPreset(value), options.DisplayIdentity!);
                            result = ScreenColorSettings.TitleFor(value);
                        }
                        else result = (ScreenColorService.Restore(options.DisplayIdentity!) | IccFilterService.Restore(options.DisplayIdentity))
                            ? Str.T("Str.PrepareOriginalIcc") : Str.T("Str.PrepareAlreadyOriginal");
                        return new(Str.T("Str.PrepareIcc"), true, result);
                    });
                    if (options.ApplyVibrance) Run(Str.T("Str.PrepareVibrance"), () =>
                    {
                        DisplayTopology.ValidateConnection(display!);
                        DigitalVibranceService.SetPercent(options.VibrancePercent, options.DisplayIdentity);
                        return new(Str.T("Str.PrepareVibrance"), true, options.VibrancePercent + "%");
                    });
                    // Clear caches last, after profile installation and other preparation work.
                    if (options.CleanMemory) Run(Str.T("Str.PrepareMemory"), () =>
                    {
                        if (options.FullMemoryCleanup)
                        {
                            memoryProgress?.Report(new("discovery", 0, 0));
                            var targets = _background.Read();
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            var full = _memory.Run(MemoryCleanupMode.Full, targets, _background, memoryProgress, cancellation);
                            var success = MemoryCleanupSummary.Succeeded(full, targets.Count);
                            var fullDetail = MemoryCleanupSummary.Format(full, targets.Count, true, true, watch.Elapsed.TotalSeconds);
                            var causes = string.Join("\n", full.Apps.Where(a => a.Error is not null).Select(a => PrivacyScrub.Sanitize(a.Error!)));
                            return new(Str.T("Str.PrepareMemory"), success, fullDetail, success ? null : full.Error ?? full.ReadError ?? (causes.Length > 0 ? causes : fullDetail));
                        }
                        var result = _memory.Clean(options.AllStandby);
                        static string Size(ulong value) => $"{value / (1024d * 1024 * 1024):F2} GiB";
                        var detail = result.After is { } after
                            ? Str.T("Str.MemoryCleaned", Size(result.Before.Available), Size(after.Available))
                            : Str.T("Str.MemoryCleanedReadFailed", result.ReadError ?? "");
                        return new(Str.T("Str.PrepareMemory"), result.After is not null, detail, result.ReadError);
                    });
                    return (IReadOnlyList<PreparationStep>)steps;
                }
                finally { _ownsMutationThread = false; }
            });
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
