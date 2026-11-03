namespace FpsTune.Wpf.Services;

/// <summary>
/// 方案热键（Ctrl+Alt+1/2/3）与托盘菜单共用的「把方案套到桌面」无头路径。
/// 与滤镜页应用栏走同一条 <see cref="ColorWorkspaceService.Apply"/>：带画面快照的方案按快照写，
/// 纯预设方案按 ForPreset 写，外部 .icc 折 vcgt。这是用户主动套的桌面方案，
/// 不走前台守护的 pending 凭证，还原走「还原原始」的既有备份契约。
/// </summary>
internal static class FilterSchemeApplier
{
    /// <summary>解析方案的滤镜（生成式预设枚举，或外部 .icc 物化后的文件路径）。</summary>
    internal static (IccFilterPreset? Preset, string? File) ResolvePreset(string presetFileName)
    {
        if (string.IsNullOrWhiteSpace(presetFileName)) return (null, null);
        if (presetFileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal))
        {
            var key = presetFileName[GameFilterLibrary.GeneratedPrefix.Length..];
            if (!Enum.TryParse<IccFilterPreset>(key, ignoreCase: false, out var preset))
                throw new InvalidOperationException(Str.T("Str.FilterPresetMissing", presetFileName));
            return (preset, null);
        }
        return (null, ScreenColorPreview.CacheIccProfile(GameFilterLibrary.ReadPreset(presetFileName)));
    }

    /// <summary>主屏身份；读不到就是真的没有可写的屏。</summary>
    internal static string DesktopIdentity()
        => DisplayTopology.GetDisplays().FirstOrDefault(d => d.Independent && d.IsPrimary)?.Identity
           ?? DisplayTopology.GetDisplays().FirstOrDefault(d => d.Independent)?.Identity
           ?? throw new InvalidOperationException(Str.T("Str.DisplayDisconnected"));

    /// <summary>把一条方案套到桌面（主屏）。返回给气泡/托盘的结果文本。</summary>
    internal static string ApplyToDesktop(FilterScheme scheme)
    {
        var (preset, file) = ResolvePreset(scheme.PresetFileName);
        if (preset is null && file is null && scheme.Vibrance is null && scheme.Screen is not { IsValid: true })
            throw new InvalidOperationException(Str.T("Str.FilterSchemeNothingToApply", scheme.Name));
        var identity = DesktopIdentity();
        var settings = scheme.Screen is { IsValid: true } screen ? screen.ToSettings()
            : preset is { } p ? ScreenColorSettings.ForPreset(p)
            : new ScreenColorSettings();
        // 滤镜页的口径：屏参数通道支持才写（不支持时只写 ICC/振动，不整个失败）。
        var applyScreen = ScreenColorService.GetState(identity).Supported;
        return ColorWorkspaceService.Apply(identity, settings, iccMode: preset is not null || file is not null,
            preset, file, scheme.Vibrance, applyScreen, restoreIcc: false);
    }

    /// <summary>
    /// 套方案库里的第 <paramref name="slot"/> 条（1 起）。用于热键与托盘：
    /// 写屏在后台线程做，结果用托盘气泡反馈（套方案的直接反馈不受「完成通知」开关影响）。
    /// </summary>
    internal static void ApplySlot(int slot)
    {
        var schemes = FilterSchemeStore.Schemes;
        var scheme = slot >= 1 && slot <= schemes.Count ? schemes[slot - 1] : null;
        if (scheme is null)
        {
            TrayService.NotifyImportant(Str.T("Str.FilterSchemeTrayTitle"),
                Str.T("Str.FilterSchemeHotkeyEmpty", slot));
            return;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var outcome = ApplyToDesktop(scheme);
                TrayService.NotifyImportant(Str.T("Str.FilterSchemeTrayTitle"),
                    Str.T("Str.FilterSchemeHotkeyApplied", slot, scheme.Name) + "\n" + outcome);
            }
            catch (Exception ex)
            {
                TrayService.NotifyImportant(Str.T("Str.FilterSchemeTrayTitle"),
                    Str.T("Str.FilterSchemeHotkeyFailed", slot, scheme.Name, PrivacyScrub.Sanitize(ex.Message)));
            }
        });
    }
}
