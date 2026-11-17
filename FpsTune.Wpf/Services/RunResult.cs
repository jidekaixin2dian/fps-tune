namespace FpsTune.Wpf.Services;

/// <summary>
/// 一次引擎动作的结果：退出码 + 输出文本（人类可读摘要或 JSON）。
/// 0 = 成功；非 0 表示失败或部分失败，具体原因在各引擎的错误文案里。
/// </summary>
public sealed record RunResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}
