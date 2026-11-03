using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

internal static class ColorWorkspaceService
{
    internal static string Apply(string identity, ScreenColorSettings settings, bool iccMode,
        IccFilterPreset? preset, string? file, int? vibrance, bool applyScreen = true, bool restoreIcc = false,
        bool startNewBaselineOnDrift = true)
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
        restoreIcc &= !iccMode;
        if (!iccMode && !applyScreen && vibrance is null && !restoreIcc)
            throw new InvalidOperationException(Str.T("Str.ColorReadFailed"));
        var outcomes = new List<string>();
        try
        {
            if (restoreIcc && IccFilterService.Restore(identity))
                outcomes.Add("ICC: " + Str.T("Str.ColorRestored"));
            if (iccMode)
            {
                var name = file is not null ? IccFilterService.ApplyFromFile(file, identity, startNewBaselineOnDrift)
                    : IccFilterService.Apply(preset!.Value, identity, startNewBaselineOnDrift);
                outcomes.Add(Str.T("Str.IccApplied", System.IO.Path.GetFileName(name)));
            }
            if (applyScreen)
            {
                // 滤镜（ICC 预设）真正能被游戏看到的那一半：把它的 vcgt/TRC 曲线折进 gamma ramp。
                // ICC 关联只影响主动读色彩管理的程序，游戏不读，所以两条路一起写。
                // 只有真的选了滤镜（ICC 模式）才折曲线；否则默认预设会把画面白白改掉。
                var filter = iccMode ? ReadFilterCurves(preset, file) : null;
                ScreenColorService.Apply(settings, identity, startNewBaselineOnDrift, preserveIcc: iccMode, filter: filter);
                outcomes.Add(Str.T("Str.ColorApplied"));
                // 只有真的折了校准 ramp（vcgt）才敢说「曲线已并入屏幕」；
                // 外部 ICC 若没有 vcgt，就只能关联——那类文件对游戏没有可见效果，如实说出来。
                if (filter is { EncodedRamp: true } && !filter.IsIdentity)
                    outcomes.Add(Str.T("Str.ColorFilterCurveApplied"));
                else if (iccMode && file is not null)
                    outcomes.Add(Str.T("Str.ColorIccNoCalibration"));
            }
            if (vibrance is { } percent)
            {
                DigitalVibranceService.SetPercent(percent, identity, startNewBaselineOnDrift);
                outcomes.Add(Str.T("Str.ColorVibranceApplied", percent));
            }
            return string.Join("\n", outcomes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(string.Join("\n", outcomes.Append(Str.T("Str.ColorWorkspaceFailed", ex.Message))), ex);
        }
    }
    /// <summary>读取要折进屏幕曲线的校准表（vcgt）；读不到或不是校准表就按「没有」处理，不阻断写入。</summary>
    private static IccCurves? ReadFilterCurves(IccFilterPreset? preset, string? file)
    {
        try
        {
            // 内置预设不折：它的 vcgt 与 ForPreset 的画面参数是同一条曲线，折两遍就套了两次。
            // 外部 .icc 才有必要折——那里没有参数可用，vcgt 是唯一能让游戏看见的东西。
            return file is null ? null : IccCurveReader.TryRead(System.IO.File.ReadAllBytes(file));
        }
        catch { return null; }
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
