using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal sealed record PreparationPlan(GamePreparationOptions Options, bool IsGameSpecific);

/// <summary>Additive game overrides; legacy settings remain the shared default.</summary>
internal static class PreparationPlanStore
{
    private sealed record SavedPlan(int SchemaVersion, string GameKey, GamePreparationOptions Options);
    internal static string FileFor(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.Any(char.IsControl)) throw new ArgumentException(Str.T("Str.PlanSelectGame"));
        return Path.Combine(UserDataPaths.Root, "preparation-plans", SessionConditionRules.CaptureGame(path, null).GameKey + ".json");
    }
    internal static PreparationPlan Resolve(string? path)
    {
        var options = string.IsNullOrWhiteSpace(path) ? null : Load(path);
        return new(options ?? SettingsService.Current.Preparation, options is not null);
    }
    private static GamePreparationOptions? Load(string path)
    {
        var file = FileFor(path);
        if (!File.Exists(file)) return null;
        try
        {
            if (new FileInfo(file).Length > 32768) throw new InvalidDataException();
            var plan = JsonSerializer.Deserialize<SavedPlan>(File.ReadAllText(file, Encoding.UTF8));
            if (plan is not { SchemaVersion: 1, Options: { } options } || !Valid(options)
                || plan.GameKey != SessionConditionRules.CaptureGame(path, null).GameKey) throw new InvalidDataException();
            return options;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new InvalidDataException(Str.T("Str.PlanUnreadable"), ex); }
    }
    internal static bool Valid(GamePreparationOptions o) => o.Configured && o.HasActions
        && (o.IccPreset is null || Enum.IsDefined(o.IccPreset.Value)) && o.VibrancePercent is >= 0 and <= 100
        && (o.DisplayIdentity is null || o.DisplayIdentity.Length <= 4096 && !o.DisplayIdentity.Any(char.IsControl))
        && (!(o.ApplyIcc || o.ApplyVibrance) || !string.IsNullOrWhiteSpace(o.DisplayIdentity))
        && (!o.ApplyOptimizations || o.OptimizationPreset is "balanced" or "safe-only")
        && (!o.ApplyFrameLimit || o.FrameLimitFps is >= 20 and <= 1000);

    internal static void Save(string? path, GamePreparationOptions options)
    {
        if (!Valid(options)) throw new ArgumentException(Str.T("Str.PrepareChoose"));
        if (path is null)
        {
            var previous = SettingsService.Current.Preparation;
            try { SettingsService.Current.Preparation = options; SettingsService.Save(SettingsService.Current); }
            catch { SettingsService.Current.Preparation = previous; throw; }
            return;
        }
        var file = FileFor(path);
        _ = Load(path);
        var saved = new SavedPlan(1, SessionConditionRules.CaptureGame(path, null).GameKey!, options);
        AtomicFile.WriteAllTextDurable(file, JsonSerializer.Serialize(saved), new UTF8Encoding(false));
    }
    internal static void UseDefault(string path)
    {
        var file = FileFor(path);
        if (File.Exists(file)) File.Move(file, file + ".disabled-" + Guid.NewGuid().ToString("N"));
    }
    internal static string Describe(PreparationPlan plan)
    {
        var o = plan.Options;
        if (!o.Configured) return Str.T("Str.PlanNotConfigured");
        return Str.T(plan.IsGameSpecific ? "Str.PlanGameSummary" : "Str.PlanDefaultSummary", DescribeActions(o));
    }
    internal static string DescribeActions(GamePreparationOptions o)
    {
        var actions = new List<string>();
        if (o.CleanMemory) actions.Add(Str.T(o.FullMemoryCleanup ? "Str.MemoryFullAction" : o.AllStandby ? "Str.MemoryAllMode" : "Str.MemoryLowMode"));
        if (o.ApplyOptimizations) actions.Add(Str.T("Str.PlanOptimizations", PresetLabel(o.OptimizationPreset)));
        if (o.ApplyPowerPlan) actions.Add(PowerPlanLabel());
        if (o.ApplyFrameLimit) actions.Add(Str.T("Str.PlanFrameLimit", o.FrameLimitFps ?? 0));
        if (o.ApplyIcc) actions.Add(o.IccPreset is { } preset ? ScreenColorSettings.TitleFor(preset) : Str.T("Str.PrepareOriginalIcc"));
        if (o.ApplyVibrance) actions.Add(Str.T("Str.PlanVibrance", o.VibrancePercent));
        return string.Join(" · ", actions);
    }
    internal static string PresetLabel(string? preset)
        => Str.T(preset == "safe-only" ? "Str.PresetSafe" : "Str.PresetBalanced");
    private static string PowerPlanLabel()
        => ItemCatalog.All.FirstOrDefault(i => i.Id == "power-ultimate")?.DisplayName ?? "power-ultimate";
}
