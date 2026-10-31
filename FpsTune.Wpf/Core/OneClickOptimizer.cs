using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>常用系统推荐入口；明确请求的旧画质组合保留为独立方法。</summary>
public static class OneClickOptimizer
{
    /// <summary>
    /// 1070 Ti 档显卡 3D 包：高质量纹理 + 最高性能优先 + 透明度 2x + 预渲染 1 帧
    /// + AF16x + VSync 关 + 着色器缓存开（P2-6 热门项）。
    /// </summary>
    public static void ApplyGpu1070TiPack(string gameExe)
    {
        DisplayQualityService.ApplyDlssPreset(gameExe, DlssPreset.PresetK);
        DisplayQualityService.ApplyTextureQuality(gameExe, TextureFilterQuality.HighQuality);
        DisplayQualityService.ApplyPowerMode(gameExe, PowerMode.PreferMax);
        DisplayQualityService.ApplyTransparencyAa(gameExe, TransparencyAa.Supersample2x);
        DisplayQualityService.ApplyPreRenderLimit(gameExe, 1);
        DisplayQualityService.ApplyAnisoLevel(gameExe, AnisoLevel.Level16);
        DisplayQualityService.ApplyVSyncMode(gameExe, VSyncMode.ForceOff);
        DisplayQualityService.ApplyShaderDiskCache(gameExe, true);
    }

    public static Task<RunResult> ApplyAsync() => ApplyAsync(AppState.GamePath);

    // The legacy explicit quality pack above remains available to callers that ask for it.
    // The default action follows the catalog recommendation and does not add driver overrides.
    public static Task<RunResult> ApplyAsync(string? gamePath)
        => OptimizationEngine.ApplyPresetAsync("balanced", gamePath);
}
