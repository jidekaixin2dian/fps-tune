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
            // 顺序很重要：写数字振动会重建显卡颜色管线，把已经写进去的 gamma 顶回原始值
            // （真机实测：ramp 写入后 1 秒内被抹平，用户看到的就是“闪一下”）。
            // 所以先写振动，最后写 gamma，gamma 写完后还要读回确认。
            if (vibrance is { } percent)
            {
                DigitalVibranceService.SetPercent(percent, identity, startNewBaselineOnDrift);
                outcomes.Add(Str.T("Str.ColorVibranceApplied", percent));
            }
            if (applyScreen)
            {
                // 滤镜（ICC 预设）真正能被游戏看到的那一半：把它的 TRC 曲线折进 gamma ramp。
                // ICC 关联只影响主动读色彩管理的程序，游戏不读，所以两条路一起写。
                // 只有真的选了滤镜（ICC 模式）才折曲线；否则默认预设会把画面白白改掉。
                var filter = iccMode ? ReadFilterCurves(preset, file) : null;
                ScreenColorService.Apply(settings, identity, startNewBaselineOnDrift, preserveIcc: iccMode, filter: filter);
                outcomes.Add(Str.T("Str.ColorApplied"));
                if (filter is not null && !filter.IsIdentity)
                    outcomes.Add(Str.T("Str.ColorFilterCurveApplied"));
                // 驱动/系统会在写入后 1–2 秒重建颜色管线，把 gamma 顶回原始值（用户看到的就是"闪一下"）。
                // 后台守 10 秒，漂了就补写；不阻塞这一步的返回。
                // 先守 3 秒确认这份 ramp 真的站得住；站不住就如实告诉用户是被系统/驱动复位了，
                // 而不是一直偷偷补写（实测本机裸写 LUT 也会在 0.6 秒内被抹平）。
                if (vibrance is not null || filter is not null)
                {
                    var stuck = ScreenColorService.EnsureRampSticks(identity, attempts: 6, delayMs: 500);
                    if (!stuck) outcomes.Add(Str.T("Str.ColorDriftAfterVibrance"));
                }
            }
            return string.Join("\n", outcomes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(string.Join("\n", outcomes.Append(Str.T("Str.ColorWorkspaceFailed", ex.Message))), ex);
        }
    }
    /// <summary>读取当前选择的滤镜曲线（.icc 文件或生成式预设）；读不到按「没有滤镜」处理，不阻断写入。</summary>
    private static IccCurves? ReadFilterCurves(IccFilterPreset? preset, string? file)
    {
        try
        {
            if (file is not null) return IccCurveReader.TryRead(System.IO.File.ReadAllBytes(file));
            if (preset is { } p) return IccCurveReader.TryRead(IccProfileGenerator.Build(p));
            return null;
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
