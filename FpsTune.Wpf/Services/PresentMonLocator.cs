using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>
/// PresentMon 查找的唯一入口（0.5.0 起，实验管线与会话采样共用）：
/// 显式指定 → 内置（解压 + SHA-256 校验）→ 已装固定路径 → 环境变量。
/// 显式路径、已装路径与环境变量路径都必须通过签名校验（TrustedCaptureTool 白名单：
/// Intel / NVIDIA 官方签名）；内置件是 Intel 官方签名版，启动进程时走同一条 OpenVerified 校验。
/// 全部落空时返回 null，由调用方给出明确的降级提示，不静默失效。
/// </summary>
internal static class PresentMonLocator
{
    /// <summary>测试注入：跳过解压，直接返回「内置采样器路径」（null 表示内置不可用）。</summary>
    internal static Func<string?>? BundledOverride;
    /// <summary>测试注入：已装候选路径清单（默认真实三处固定位置）。</summary>
    internal static Func<string[]>? InstalledCandidatesOverride;
    /// <summary>测试注入：环境变量读取（默认读 {EnvVariable}）。</summary>
    internal static Func<string?>? EnvironmentOverride;

    /// <summary>环境变量名：用户自带采样器时指定完整路径（仍要求签名校验通过，不从 PATH 搜索）。</summary>
    internal const string EnvVariable = "FPS_TUNE_PRESENTMON";

    internal static string? Find(string? explicitPath = null)
    {
        if (explicitPath is { Length: > 0 })
            return Path.IsPathFullyQualified(explicitPath) && TrustedCaptureTool.IsTrusted(explicitPath) ? explicitPath : null;

        // 1) 内置：解压 + 哈希校验。失败（资源缺失/磁盘不可写/哈希不符）记录后回落到已装采样器。
        string? bundled = null;
        try { bundled = BundledOverride is { } bundledInjected ? bundledInjected() : BundledToolExtract.EnsurePresentMon(); }
        catch (Exception ex) { ErrorLog.Write("presentmon-bundled", ex); }
        if (bundled is not null && File.Exists(bundled))
            return bundled;

        // 2) 已装：固定安装位置 + 签名验证（与 0.4.x 一致）。
        var candidates = InstalledCandidatesOverride?.Invoke() ?? new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WinGet\Links\presentmon.exe"),
        };
        var installed = candidates.FirstOrDefault(p => File.Exists(p) && TrustedCaptureTool.IsTrusted(p));
        if (installed is not null)
            return installed;

        // 3) 环境变量：高级用户自带采样器。
        var fromEnv = EnvironmentOverride?.Invoke() ?? Environment.GetEnvironmentVariable(EnvVariable);
        if (fromEnv is { Length: > 0 } && Path.IsPathFullyQualified(fromEnv) && File.Exists(fromEnv)
            && TrustedCaptureTool.IsTrusted(fromEnv))
            return fromEnv;

        return null;
    }

    /// <summary>
    /// 只读探测（A/B 页前置提示、会话采样共用）：内置不可用时自动回落已装采样器。
    /// 探测会触发内置解压（保证首次正式采样零等待）；解压失败不视为错误。
    /// </summary>
    public static string? Probe() => Find(null);
}
