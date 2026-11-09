using System.Text.RegularExpressions;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 优化项失败原因的界面口径：把 .NET / Win32 的原始异常翻译成用户能照着做的下一步。
///
/// 为什么需要它：优化项逐项执行，失败原因直接来自异常 <c>Message</c>——
/// 非管理员写 HKLM 会拿到 “不允许所请求的注册表访问权”，被策略挡住会拿到
/// “Access is denied”。这些原文写在执行结果里既看不懂，也看不出该做什么。
/// 已经是我们自己写的中文提示则原样保留（不重复套壳）；原始文本另存
/// <see cref="OptimizationApplyResult.Detail"/> 进诊断回执，排查时照样查得到。
///
/// 中文 Windows 的"访问被拒绝"由 <see cref="UnauthorizedAccessException"/> /
/// <see cref="System.Security.SecurityException"/> 承担（类型判断先于文本匹配），
/// sc.exe / powercfg 这类命令行返回的拒绝访问会连着调用方的中文前缀一起原样输出，
/// 所以这里只匹配英文文本，不在代码里堆中文字面量。
/// </summary>
internal static class OptimizationFailureText
{
    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]");

    private static readonly string[] AccessDenied =
    [
        "access is denied",
        "access denied",
        "requested registry access is not allowed",
        "registry key access is denied",
        "cannot write to the registry key"
    ];

    private static readonly string[] Missing =
    [
        "the system cannot find the file specified",
        "the system cannot find the path specified",
        "cannot find the file"
    ];

    private static readonly string[] Unsupported =
    [
        "the parameter is incorrect",
        "not supported"
    ];

    internal static string Describe(Exception error) => error switch
    {
        UnauthorizedAccessException => Str.T("Str.OptFailNeedAdmin"),
        System.Security.SecurityException => Str.T("Str.OptFailNeedAdmin"),
        _ => Describe(error.Message)
    };

    internal static string Describe(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return Str.T("Str.OptFailUnknown");

        var lower = text.ToLowerInvariant();
        if (AccessDenied.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return Str.T("Str.OptFailNeedAdmin");
        if (Missing.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return Str.T("Str.OptFailMissing");
        if (Unsupported.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return Str.T("Str.OptFailUnsupported");

        // 中文提示是我们自己写给人看的（读回不一致、平台不支持、命令行拒绝访问等），不套第二层壳。
        if (Cjk.IsMatch(text))
            return text;

        return Str.T("Str.OptFailWithDetail", text);
    }
}
