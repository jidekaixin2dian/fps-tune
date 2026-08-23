namespace DeltaForceTune.Wpf.Core;

using DeltaForceTune.Wpf.Services;

/// <summary>
/// C# 核心引擎入口。
/// 当前作为 WPF 与底层执行层之间的统一抽象；后续会逐步把 PowerShell 实现替换为原 C# 实现。
/// </summary>
public static class OptimizationEngine
{
    public static Task<RunResult> DetectAsync()
        => PowerShellRunner.RunAsync(ScriptLocator.Resolve("delta-optimizer.ps1"), "-Detect", "-Json");

    public static Task<RunResult> ApplyPresetAsync(string preset)
        => PowerShellRunner.RunAsync(
            ScriptLocator.Resolve("delta-optimizer.ps1"),
            "-Apply", "-Preset", preset, "-Force", "-Json");

    public static Task<RunResult> ApplyItemsAsync(IEnumerable<string> ids)
        => PowerShellRunner.RunAsync(
            ScriptLocator.Resolve("delta-optimizer.ps1"),
            "-Apply", "-Items", string.Join(",", ids), "-Force", "-Json");

    public static Task<RunResult> RestoreAsync()
        => PowerShellRunner.RunAsync(ScriptLocator.Resolve("delta-optimizer.ps1"), "-Restore", "-Json");

    public static Task<RunResult> ListRestoreAsync()
        => PowerShellRunner.RunAsync(ScriptLocator.Resolve("delta-optimizer.ps1"), "-ListRestoreItems", "-Json");
}
