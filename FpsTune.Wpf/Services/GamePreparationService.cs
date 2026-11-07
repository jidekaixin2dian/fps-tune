using System.IO;
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
    public bool ApplyOptimizations { get; init; }
    /// <summary>基础优化项预设名（catalog 预设，唯一来源）；空串/按均衡处理仅用于旧数据兜底。</summary>
    public string? OptimizationPreset { get; init; }
    public bool ApplyFrameLimit { get; init; }
    public uint? FrameLimitFps { get; init; }
    public bool ApplyPowerPlan { get; init; }
    [JsonIgnore] public bool HasActions => CleanMemory || ApplyIcc || ApplyVibrance || ApplyOptimizations || ApplyFrameLimit || ApplyPowerPlan;
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
    internal bool NeedsAdministrator(GamePreparationOptions options)
    {
        if (options.CleanMemory && !_memory.IsAdministrator) return true;
        try { return OptimizationIdsFor(options).Any(NeedsAdmin); }
        catch (Exception) { return true; } // 预设无法解析按需管理员处理；RunAsync 会给出真实错误
    }
    private static bool NeedsAdmin(string id) =>
        ItemCatalog.All.FirstOrDefault(i => i.Id == id) is { Admin: true, Available: true };

    /// <summary>开局应用的优化项：基础预设 + 电源计划；去重保持顺序。</summary>
    internal static IReadOnlyList<string> OptimizationIdsFor(GamePreparationOptions options)
    {
        var ids = new List<string>();
        if (options.ApplyOptimizations)
            ids.AddRange(OptimizationCatalog.ResolvePreset(
                string.IsNullOrWhiteSpace(options.OptimizationPreset) ? "balanced" : options.OptimizationPreset!));
        if (options.ApplyPowerPlan)
            ids.Add("power-ultimate");
        return ids.Distinct().ToArray();
    }

    internal async Task<IReadOnlyList<PreparationStep>> RunAsync(GamePreparationOptions options, IProgress<PreparationStep>? progress = null,
        IProgress<MemoryCleanupProgress>? memoryProgress = null, CancellationToken cancellation = default, string? gamePath = null)
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
                    if (options.ApplyFrameLimit && options.FrameLimitFps is not (>= 20 and <= 1000))
                        throw new InvalidOperationException(Str.T("Str.PrepareFrameLimitRange"));
                    if (options.ApplyFrameLimit && string.IsNullOrWhiteSpace(gamePath))
                        throw new InvalidOperationException(Str.T("Str.PrepareFrameLimitNeedsGame"));
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
                    // 同步复用引擎：本工作线程已持有 SystemMutationGate，同线程重入安全。
                    var optimizationIds = OptimizationIdsFor(options);
                    if (optimizationIds.Count > 0) Run(Str.T("Str.PrepareSystemStep"), () =>
                    {
                        var receipt = OptimizationEngine.ApplyItemsWithReceipt(optimizationIds, gamePath);
                        var results = receipt.Results;
                        // 与引擎 CLI 汇总口径一致：跳过不算失败（如共用方案下缺少游戏路径的逐游戏项）。
                        var ok = results.All(r => r.Ok || r.Skipped);
                        var summary = Str.T("Str.PrepareSystemSummary", results.Count(r => r.Ok),
                            results.Count(r => !r.Ok && !r.Skipped), results.Count(r => r.Skipped));
                        var detail = summary + "\n" + string.Join("\n",
                            results.Select(r => Str.T("Str.PrepareSystemItem", r.Name, r.Message)));
                        var error = results.FirstOrDefault(r => !r.Ok && !r.Skipped) is { } failed
                            ? PrivacyScrub.Sanitize(failed.Message) : null;
                        return new(Str.T("Str.PrepareSystemStep"), ok, detail, error);
                    });
                    if (options.ApplyFrameLimit) Run(Str.T("Str.PrepareFrameLimitStep"), () =>
                    {
                        var exe = Path.GetFileName(gamePath!);
                        DisplayQualityService.ApplyFrameLimits(exe, new(options.FrameLimitFps!.Value, null)).RequireApplied();
                        return new(Str.T("Str.PrepareFrameLimitStep"), true, Str.T("Str.PlanFrameLimit", options.FrameLimitFps!.Value));
                    });
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
                            // 必须走滤镜页同一个入口：先前这里直接调 ScreenColorService.Apply，
                            // 那条路 preserveIcc 默认 false，会先把滤镜页已关联的 ICC 预设还原掉、
                            // 备份也删掉，然后只写 gamma——准备结果却报「切换画面滤镜成功」。
                            ColorWorkspaceService.Apply(options.DisplayIdentity!,
                                ScreenColorSettings.ForPreset(value), iccMode: true,
                                preset: value, file: null, vibrance: null);
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
