using System.IO;
using System.Text;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 一键优化（用户定稿 2026-09-22）：
/// ① catalog 均衡档（含电源计划→卓越性能 power-ultimate、游戏强制高性能 GPU gpu-pref）
/// ② DLSS K 模型（社区常用）
/// ③ 显卡 3D 按 **1070 Ti 档**（非 5070 Ti 桌面 4x 档）：纹理高质量 + 电源最高性能优先 + 透明度 2x + 预渲染 1
/// ④ 电源方案随均衡档 power-ultimate 一并应用
/// 不伪装显卡型号（红线二）；「1070 Ti」指 3D 设置档位标签。
/// </summary>
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

    public static Task<RunResult> ApplyAsync(string? gamePath) => Task.Run(() =>
    {
        var sb = new StringBuilder();
        var exit = 0;

        // ① 均衡档系统层（含 power-ultimate 电源方案 / gpu-pref 高性能 GPU）
        var catalog = OptimizationEngine.ApplyPresetAsync("balanced", gamePath).GetAwaiter().GetResult();
        exit = catalog.ExitCode;
        sb.AppendLine("== 均衡档系统层 ==");
        sb.AppendLine(string.IsNullOrWhiteSpace(catalog.Output) ? "（无输出）" : catalog.Output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(catalog.Error))
        {
            sb.AppendLine("[错误] " + catalog.Error.TrimEnd());
            exit = 1;
        }

        // ②③ 显卡 3D：DLSS K + 1070 Ti 档
        if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath))
        {
            sb.AppendLine();
            sb.AppendLine("== 显卡 3D（1070 Ti 档）==");
            sb.AppendLine("[跳过] 尚未定位游戏主程序；先到「检测」页定位后再一键，或到「显示与画质」单独应用。");
            return new RunResult(exit, sb.ToString(), "");
        }

        if (!DisplayQualityService.FeatureEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("== 显卡 3D（1070 Ti 档）==");
            sb.AppendLine("[跳过] 驱动设置功能已停用（FPS_ENABLE_DLSS=0）。");
            return new RunResult(exit, sb.ToString(), "");
        }

        if (!DisplayQualityService.IsNvidiaSupported)
        {
            sb.AppendLine();
            sb.AppendLine("== 显卡 3D（1070 Ti 档）==");
            sb.AppendLine("[跳过] 未检测到 NVIDIA 驱动（A 卡/Intel 请用显示页 ICC 滤镜）。");
            return new RunResult(exit, sb.ToString(), "");
        }

        try
        {
            ApplyGpu1070TiPack(Path.GetFileName(gamePath));
            sb.AppendLine();
            sb.AppendLine("== 显卡 3D（1070 Ti 档）==");
            sb.AppendLine("[已应用] DLSS K 模型 · 纹理高质量 · 电源最高性能优先 · 透明度 2x · 预渲染 1 帧");
            sb.AppendLine("电源方案：卓越性能（随均衡档 power-ultimate）");
            sb.AppendLine("可在「显示与画质」微调或一键还原。");
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            sb.AppendLine();
            sb.AppendLine("== 显卡 3D（1070 Ti 档）==");
            sb.AppendLine("[失败] 写入 NVIDIA 配置需要管理员权限；系统层均衡档已应用。");
            sb.AppendLine(ex.Message);
            exit = 1;
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("== 显卡 3D（1070 Ti 档）==");
            sb.AppendLine("[失败] " + ex.Message);
            sb.AppendLine("系统层均衡档已应用；显卡 3D 可稍后重试。");
            exit = 1;
        }

        return new RunResult(exit, sb.ToString(), "");
    });
}
