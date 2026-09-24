namespace FpsTune.Wpf.Services;

/// <summary>
/// 一次"跑外部命令/引擎动作"的结果：退出码 + stdout + stderr。
///
/// 历史：这个类型原先和 `PowerShellRunner` 放在同一个文件里。2026-09-25 删掉朋友测试模块时，
/// `PowerShellRunner` 与 `ScriptLocator` 一并移除（它们只服务那一处），
/// 但 `RunResult` 是**引擎与多个视图共用的通用返回类型**，所以单独留在这里。
/// </summary>
public sealed record RunResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}
