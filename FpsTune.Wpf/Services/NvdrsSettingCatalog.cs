namespace FpsTune.Wpf.Services;

/// <summary>参数取值形态：固定枚举，或一个数值区间。</summary>
public enum NvdrsSettingKind
{
    Choice,
    Range,
}

/// <summary>
/// 枚举参数的一个可选取值；Value 为写进驱动的原始值。
/// LabelKey 以 <c>Str.</c> 开头时按资源键取文案，否则原样显示（纯数字倍数如 2x/16x 不用翻译）。
/// </summary>
public sealed record NvdrsChoice(uint Value, string LabelKey)
{
    public string Label => LabelKey.StartsWith("Str.", StringComparison.Ordinal) ? Str.T(LabelKey) : LabelKey;
}

/// <summary>
/// 一条高级驱动参数：名称、取值形态、默认值、一句话说明。
/// 名称与取值都走资源字典，界面切语言时一起变。
/// </summary>
public sealed record NvdrsSettingDefinition(
    uint SettingId,
    string NameKey,
    NvdrsSettingKind Kind,
    IReadOnlyList<NvdrsChoice> Choices,
    uint Min,
    uint Max,
    uint? DriverDefault,
    string NoteKey)
{
    public string Name => Str.T(NameKey);
    public string Note => Str.T(NoteKey);
    public bool IsChoice => Kind == NvdrsSettingKind.Choice;
}

/// <summary>
/// 高级参数的唯一来源（SettingID → 名称 / 取值 / 默认值 / 说明）。
/// 只收本仓库既有的、已在真实驱动上读写过的设置项：ID 与枚举值都来自
/// <see cref="DisplayQualityService"/> 里的官方常量，不另外猜 ID。
/// </summary>
public static class NvdrsSettingCatalog
{
    private static readonly NvdrsChoice[] TextureChoices =
    {
        new((uint)TextureFilterQuality.HighQuality, "Str.TextureHighQuality"),
        new((uint)TextureFilterQuality.Quality, "Str.TextureQuality"),
        new((uint)TextureFilterQuality.Performance, "Str.TexturePerformance"),
        new((uint)TextureFilterQuality.HighPerformance, "Str.TextureHighPerformance"),
    };

    private static readonly NvdrsChoice[] PowerChoices =
    {
        new((uint)PowerMode.PreferMax, "Str.PowerPreferMax"),
        new((uint)PowerMode.Adaptive, "Str.PowerAdaptive"),
        new((uint)PowerMode.DriverControlled, "Str.PowerDriverControlled"),
        new((uint)PowerMode.PreferConsistentPerformance, "Str.PowerStablePerf"),
        new((uint)PowerMode.PreferMin, "Str.PowerLowestConsumption"),
        new((uint)PowerMode.OptimalPower, "Str.PowerOptimal"),
    };

    private static readonly NvdrsChoice[] MultisampleChoices =
    {
        new(DisplayQualityService.TransparencyMultisampleOn, "Str.AaMultisample"),
        new(0, "Str.Off"),
    };

    private static readonly NvdrsChoice[] SupersampleChoices =
    {
        new(DisplayQualityService.TransparencySupersample2x, "2x"),
        new(DisplayQualityService.TransparencySupersample4x, "4x"),
        new(0, "Str.Off"),
    };

    private static readonly NvdrsChoice[] AnisoSelectorChoices =
    {
        new(DisplayQualityService.AnisoSelectorUser, "Str.RecipeDriver"),
        new(0, "Str.AppControlled"),
    };

    private static readonly NvdrsChoice[] AnisoLevelChoices =
    {
        new(DisplayQualityService.AnisoLevel16x, "16x"),
        new((uint)AnisoLevel.Level8, "8x"),
        new((uint)AnisoLevel.Level4, "4x"),
        new((uint)AnisoLevel.Level2, "2x"),
        new((uint)AnisoLevel.Off, "Str.Off"),
        new((uint)AnisoLevel.AppControlled, "Str.AppControlled"),
    };

    private static readonly NvdrsChoice[] VSyncChoices =
    {
        new(DisplayQualityService.VSyncForceOff, "Str.VSyncForceOff"),
        new(0x47814940u, "Str.VSyncForceOn"),
        new(0u, "Str.AppControlled"),
    };

    private static readonly NvdrsChoice[] ShaderCacheChoices =
    {
        new(DisplayQualityService.ShaderCacheOn, "Str.OnRecommended"),
        new(0u, "Str.Off"),
    };

    private static readonly NvdrsChoice[] DlssPresetChoices =
    {
        new((uint)DlssPreset.Latest, "Str.PresetLatest"),
        new((uint)DlssPreset.PresetM, "Str.DlssPresetM"),
        new((uint)DlssPreset.PresetK, "Str.DlssPresetK"),
        new((uint)DlssPreset.PresetJ, "Str.DlssPresetJ"),
        new((uint)DlssPreset.PresetE, "Str.DlssPresetE"),
        new((uint)DlssPreset.FollowGame, "Str.QuickOptDlssFollow"),
    };

    public static IReadOnlyList<NvdrsSettingDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new NvdrsSettingDefinition(DisplayQualityService.TextureQualityId, "Str.RecipeTexture",
            NvdrsSettingKind.Choice, TextureChoices, 0, 0, (uint)TextureFilterQuality.Quality, "Str.AdvancedNoteTexture"),
        new NvdrsSettingDefinition(DisplayQualityService.PowerModeId, "Str.RecipePower",
            NvdrsSettingKind.Choice, PowerChoices, 0, 0, null, "Str.AdvancedNotePower"),
        new NvdrsSettingDefinition(DisplayQualityService.TransparencyMultisampleId, "Str.RecipeMultisample",
            NvdrsSettingKind.Choice, MultisampleChoices, 0, 0, 0, "Str.AdvancedNoteMultisample"),
        new NvdrsSettingDefinition(DisplayQualityService.TransparencySupersampleId, "Str.RecipeSupersample",
            NvdrsSettingKind.Choice, SupersampleChoices, 0, 0, 0, "Str.AdvancedNoteSupersample"),
        new NvdrsSettingDefinition(DisplayQualityService.PreRenderLimitId, "Str.RecipeFrames",
            NvdrsSettingKind.Range, Array.Empty<NvdrsChoice>(), 1, 6, null, "Str.AdvancedNotePreRender"),
        new NvdrsSettingDefinition(DisplayQualityService.AnisoSelectorId, "Str.RecipeAnisoSource",
            NvdrsSettingKind.Choice, AnisoSelectorChoices, 0, 0, 0, "Str.AdvancedNoteAnisoSource"),
        new NvdrsSettingDefinition(DisplayQualityService.AnisoLevelId, "Str.RecipeAniso",
            NvdrsSettingKind.Choice, AnisoLevelChoices, 0, 0, (uint)AnisoLevel.AppControlled, "Str.AdvancedNoteAniso"),
        new NvdrsSettingDefinition(DisplayQualityService.VSyncModeId, "Str.RecipeVSync",
            NvdrsSettingKind.Choice, VSyncChoices, 0, 0, 0, "Str.AdvancedNoteVSync"),
        new NvdrsSettingDefinition(DisplayQualityService.ShaderDiskCacheId, "Str.RecipeCache",
            NvdrsSettingKind.Choice, ShaderCacheChoices, 0, 0, null, "Str.AdvancedNoteShaderCache"),
        new NvdrsSettingDefinition(DisplayQualityService.DlssSrPresetId, "Str.AdvancedDlssPreset",
            NvdrsSettingKind.Choice, DlssPresetChoices, 0, 0, (uint)DlssPreset.FollowGame, "Str.AdvancedNoteDlssPreset"),
        new NvdrsSettingDefinition(DisplayQualityService.FrameRateLimitId, "Str.FrameForeground",
            NvdrsSettingKind.Range, Array.Empty<NvdrsChoice>(), 0, 1000, 0, "Str.AdvancedNoteForegroundLimit"),
        new NvdrsSettingDefinition(DisplayQualityService.BackgroundFrameRateLimitId, "Str.FrameBackground",
            NvdrsSettingKind.Range, Array.Empty<NvdrsChoice>(), 0, 200, 0, "Str.AdvancedNoteBackgroundLimit"),
    });

    public static NvdrsSettingDefinition? Find(uint settingId)
    {
        foreach (var definition in All)
            if (definition.SettingId == settingId)
                return definition;
        return null;
    }

    /// <summary>参数名；未收录的 ID 用十六进制，避免显示空白。</summary>
    public static string DisplayName(uint settingId)
        => Find(settingId)?.Name ?? Str.T("Str.AdvancedUnknownSetting", settingId.ToString("X8"));

    /// <summary>把驱动里的原始值翻译成当前语言的说法。</summary>
    public static string Describe(uint settingId, uint value)
    {
        var definition = Find(settingId);
        if (definition is null)
            return "0x" + value.ToString("X8");
        if (definition.Kind == NvdrsSettingKind.Range)
            return value.ToString();
        foreach (var choice in definition.Choices)
            if (choice.Value == value)
                return choice.Label;
        return "0x" + value.ToString("X8");
    }

    /// <summary>写前校验：区间项在范围内，枚举项必须是列出的取值之一。</summary>
    public static bool IsValid(NvdrsSettingDefinition definition, uint value)
        => definition.Kind == NvdrsSettingKind.Range
            ? value >= definition.Min && value <= definition.Max
            : definition.Choices.Any(c => c.Value == value);
}
