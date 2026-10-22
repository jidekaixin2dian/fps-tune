using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal static class ColorWorkspaceService
{
    internal static string Apply(string identity, ScreenColorSettings settings, bool iccMode,
        IccFilterPreset? preset, string? file, int? vibrance, bool applyScreen = true)
    {
        settings.Validate();
        using var gate = SystemMutationGate.Acquire();
        DisplayTopology.Require(identity);
        if (iccMode)
        {
            var state = IccFilterService.GetState(identity);
            if (!state.CanApply) throw new InvalidOperationException(state.UnsupportedReason);
            if (preset is null && file is null) throw new InvalidOperationException(Str.T("Str.ColorSelectIcc"));
        }
        if (applyScreen)
        {
            var state = ScreenColorService.GetState(identity);
            if (!state.Supported) throw new InvalidOperationException(state.Detail);
        }
        if (vibrance is not null && !DigitalVibranceService.GetState(identity).Supported)
            throw new InvalidOperationException(Str.T("Str.DisplayDvcUnavailable"));
        if (!iccMode && !applyScreen && vibrance is null)
            throw new InvalidOperationException(Str.T("Str.ColorReadFailed"));
        var outcomes = new List<string>();
        try
        {
            if (iccMode)
            {
                var name = file is not null ? IccFilterService.ApplyFromFile(file, identity, true)
                    : IccFilterService.Apply(preset!.Value, identity, true);
                outcomes.Add(Str.T("Str.IccApplied", System.IO.Path.GetFileName(name)));
            }
            if (applyScreen)
            {
                ScreenColorService.Apply(settings, identity, true, preserveIcc: iccMode);
                outcomes.Add(Str.T("Str.ColorApplied"));
            }
            if (vibrance is { } percent)
            {
                DigitalVibranceService.SetPercent(percent, identity, true);
                outcomes.Add(Str.T("Str.ColorVibranceApplied", percent));
            }
            return string.Join("\n", outcomes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(string.Join("\n", outcomes.Append(Str.T("Str.ColorWorkspaceFailed", ex.Message))), ex);
        }
    }
    internal static string Restore(string identity)
    {
        using var gate = SystemMutationGate.Acquire();
        var outcomes = new List<string>(); var failed = false;
        void Restore(string name, Func<bool> action)
        {
            try { if (action()) outcomes.Add(name + ": " + Str.T("Str.ColorRestored")); }
            catch (Exception ex) { failed = true; outcomes.Add(name + ": " + ex.Message); }
        }
        Restore(Str.T("Str.ColorPanelTitle"), () => ScreenColorService.Restore(identity));
        Restore("ICC", () => IccFilterService.Restore(identity));
        Restore(Str.T("Str.VibCard"), () => DigitalVibranceService.Restore(identity));
        var text = outcomes.Count == 0 ? Str.T("Str.ColorNoBackup") : string.Join("\n", outcomes);
        if (failed) throw new InvalidOperationException(text);
        return text;
    }
}
