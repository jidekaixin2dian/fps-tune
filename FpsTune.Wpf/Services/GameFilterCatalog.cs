using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>滤镜选择器的单一数据源：内置 .icc、生成式枚举和用户文件共享同一份描述。</summary>
public sealed record GameFilterCatalogItem(
    string FileName, string Name, string Category, string Fit, string Avoid,
    IccFilterPreset? Generated, int? Vibrance);

public static class GameFilterCatalog
{
    /// <summary>每次按现有库缓存装配，新增 ICC 或用户预设无需在多个 View 各改一份分类表。</summary>
    public static IReadOnlyList<GameFilterCatalogItem> All
    {
        get
        {
            var list = new List<GameFilterCatalogItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in GameFilterLibrary.Presets)
            {
                if (!seen.Add(preset.FileName)) continue;
                IccFilterPreset? generated = null;
                if (preset.FileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal)
                    && Enum.TryParse<IccFilterPreset>(preset.FileName[GameFilterLibrary.GeneratedPrefix.Length..], out var parsed))
                    generated = parsed;
                list.Add(new(preset.FileName, preset.Name, preset.Category, preset.Fit, preset.Avoid, generated,
                    preset.Vibrance ?? (generated is { } value ? IccProfileGenerator.VibranceFor(value) : null)));
            }
            foreach (var preset in Enum.GetValues<IccFilterPreset>())
            {
                var fileName = GameFilterLibrary.GeneratedPrefix + preset;
                if (!seen.Add(fileName)) continue;
                var keys = IccProfileGenerator.ResourceKeysFor(preset);
                list.Add(new(fileName, Str.T(keys.Title), CategoryFor(preset), Str.T(keys.Description), "", preset,
                    IccProfileGenerator.VibranceFor(preset)));
            }
            return list;
        }
    }

    /// <summary>生成式预设的显示分类，与旧滤镜页保持同一语义。</summary>
    public static string CategoryFor(IccFilterPreset preset) => preset switch
    {
        IccFilterPreset.Vivid or IccFilterPreset.Warm or IccFilterPreset.Cool or IccFilterPreset.Muted => "color",
        IccFilterPreset.ShadowBoost or IccFilterPreset.Dehaze => "bright",
        IccFilterPreset.NightGuard or IccFilterPreset.Soft or IccFilterPreset.ShadowGentle or IccFilterPreset.ContrastGentle => "soft",
        IccFilterPreset.Neutral => "game",
        IccFilterPreset.Deexposure or IccFilterPreset.DeexposureStrong => "deexpose",
        IccFilterPreset.Daylight or IccFilterPreset.Bright or IccFilterPreset.SoftBright or IccFilterPreset.ClearBright
            or IccFilterPreset.ClearBright2 or IccFilterPreset.Lively => "bright",
        IccFilterPreset.VividLively or IccFilterPreset.Cinema => "color",
        IccFilterPreset.NightScene or IccFilterPreset.Foggy => "deexpose",
        IccFilterPreset.ArenaCool or IccFilterPreset.GameGenshin or IccFilterPreset.GameKarakin or IccFilterPreset.GameGw2
            or IccFilterPreset.GameApex or IccFilterPreset.GameNaraka or IccFilterPreset.GameMyth or IccFilterPreset.GameR6
            or IccFilterPreset.GameValorant or IccFilterPreset.GameNaraka2 or IccFilterPreset.GameCod16 => "game",
        IccFilterPreset.VipSaturated or IccFilterPreset.VipDeepVivid or IccFilterPreset.VipWarmClear
            or IccFilterPreset.VipCoolSuppress => "color",
        _ => "color",
    };
}
