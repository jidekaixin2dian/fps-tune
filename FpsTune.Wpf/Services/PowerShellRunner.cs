using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FpsTune.Wpf.Services;

public sealed record RunResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

public static class PowerShellRunner
{
    // 启动器是完全常量：脚本路径、参数与期望哈希只经环境变量传递。
    // 旧实现把路径与参数拼进 %TEMP% 下的包装脚本再由 PowerShell 读取，同账户进程可以改写那个文件，
    // 提权会话下等于把管理员执行权交出去；现在既不落盘，也不需要 Windows 命令行转义。
    //
    // 关键一步：由"真正执行脚本的子进程"自己按可信哈希校验。
    //   父进程校验 + 租约 → 挡住"静态改写"整个类别；
    //   子进程自校验     → 挡住"父进程校验之后"发生的替换/换目录（否则父进程验的是 A、子进程跑的是 B）。
    // 残留窗口只剩子进程内部"算完哈希 → 按路径加载脚本"之间的极短时间；要彻底消除它，
    // 只能把脚本放进非提权账户不可写的目录（需要 ACL 编程）或改为从内存执行（会破坏 $PSScriptRoot）。
    // 这个残留窗口需要本机同账户攻击者在微秒级抢占，且届时内容不匹配会被拒绝执行而不是静默运行。
    // 参数以"名字 + 值"逐个放进环境变量，子进程组装成哈希表后用具名 splatting 调用脚本：
    // 值始终是数据，既不参与命令行、也不参与任何代码文本（数组 splatting 会把 -Name 当位置参数，
    // 具名参数会全部失效，所以这里必须是 hashtable splatting）。
    internal const string LaunchCommand =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        "$p=$env:FPSTUNE_LAUNCH_SCRIPT; $sh=$env:FPSTUNE_LAUNCH_SHA256; $ok=$false; $why=''; " +
        // 失败必须能区分三类原因，否则 CI 上无法定位：环境变量没传到、脚本文件读不到、内容确实不符。
        // 只有"读不到"重试；读到但不匹配是最终结论，绝不重试、绝不放行。
        "if (-not $p) { $why='脚本路径环境变量缺失' } " +
        "elseif (-not $sh) { $why='校验值环境变量缺失' } " +
        "else { for($t=0; $t -lt 10; $t++){ $h=$null; " +
        "try { $h=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } catch { $why=$_.Exception.Message }; " +
        "if ($h) { if ($h -eq $sh) { $ok=$true; $why='' } else { $why='内容与可信校验值不符' }; break }; " +
        "Start-Sleep -Milliseconds 250 } }; " +
        "if (-not $ok) { [Console]::Error.WriteLine('FPS 帧律：脚本完整性校验未通过（' + $why + '），已拒绝执行。'); exit 126 }; " +
        "$splat=@{}; for($i=0; $i -lt [int]$env:FPSTUNE_LAUNCH_PARAM_COUNT; $i++){ " +
        "$n=[Environment]::GetEnvironmentVariable('FPSTUNE_LAUNCH_PARAM_'+$i+'_NAME'); " +
        "if ([Environment]::GetEnvironmentVariable('FPSTUNE_LAUNCH_PARAM_'+$i+'_FLAG') -eq '1') " +
        "{ $splat[$n]=$true } else " +
        "{ $splat[$n]=[string][Environment]::GetEnvironmentVariable('FPSTUNE_LAUNCH_PARAM_'+$i+'_VALUE') } }; " +
        "& $p @splat";

    internal const string ScriptPathVariable = "FPSTUNE_LAUNCH_SCRIPT";
    internal const string ScriptHashVariable = "FPSTUNE_LAUNCH_SHA256";
    internal const string ParamCountVariable = "FPSTUNE_LAUNCH_PARAM_COUNT";
    internal const string ParamVariablePrefix = "FPSTUNE_LAUNCH_PARAM_";
    internal const string ParamNameSuffix = "_NAME";
    internal const string ParamValueSuffix = "_VALUE";
    internal const string ParamFlagSuffix = "_FLAG";

    /// <summary>环境块有硬上限（约 32K 字符），超过必须明确报错，绝不退化成写临时脚本。</summary>
    internal const int MaxLaunchPayloadChars = 8000;

    /// <summary>子进程判定"脚本内容校验失败"时使用的退出码。</summary>
    public const int ScriptIntegrityExitCode = 126;

    public static async Task<RunResult> RunAsync(
        string scriptPath,
        string[] args,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "delta-tune-wpf-tmp");

        Directory.CreateDirectory(tempDir);
        CleanupStaleOutput(tempDir);

        var name = $"{Path.GetFileNameWithoutExtension(scriptPath)}_{Guid.NewGuid():N}";
        var stdout = Path.Combine(tempDir, name + ".out.txt");
        var stderr = Path.Combine(tempDir, name + ".err.txt");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + LaunchCommand + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = root
        };
        ApplyLaunchEnvironment(psi, scriptPath, args, expectedSha256);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 PowerShell 进程。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 取消必须结束脚本及其由脚本启动的 PresentMon/引擎子进程，
            // 否则用户虽看到“已取消”，后台采样仍会继续占用资源。
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能在取消与 Kill 之间自然退出。
            }

            try { await process.WaitForExitAsync(); } catch { }
            try { await outputTask; } catch { }
            try { await errorTask; } catch { }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;

        var safeOutput = SanitizeProcessText(output, scriptPath);
        var safeError = SanitizeProcessText(error, scriptPath);
        var exitCode = process.ExitCode;

        // 成功时不落盘；失败时只保留已脱敏的输出便于排查。
        if (exitCode != 0)
        {
            if (!string.IsNullOrEmpty(safeOutput))
                File.WriteAllText(stdout, safeOutput, new UTF8Encoding(true));
            if (!string.IsNullOrEmpty(safeError))
                File.WriteAllText(stderr, safeError, new UTF8Encoding(true));
        }

        return new RunResult(exitCode, safeOutput, safeError);
    }

    /// <summary>
    /// 脚本路径、具名参数与"期望哈希"只走环境变量：不落盘、不拼 Windows 命令行、不做引号转义。
    /// <paramref name="expectedSha256"/> 应为可信来源（内置资源）的哈希；为空时退化为
    /// "启动时本地哈希"，仍能发现启动之后发生的替换，但无法判断脚本本身是否可信。
    /// </summary>
    internal static void ApplyLaunchEnvironment(
        ProcessStartInfo psi, string scriptPath, string[] args, string? expectedSha256)
    {
        var fullPath = Path.GetFullPath(scriptPath);
        var hash = expectedSha256;
        if (string.IsNullOrWhiteSpace(hash))
        {
            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法读取脚本内容以计算校验值，已拒绝启动：" + fullPath, ex);
            }
        }

        var parameters = ParseArguments(args);

        // 环境块有硬上限；超限时明确失败，而不是退化到"写临时脚本"那条可被劫持的路。
        var payload = fullPath.Length + hash.Length + ScriptPathVariable.Length + ScriptHashVariable.Length +
                      ParamCountVariable.Length + 64;
        foreach (var (name, value) in parameters)
            payload += name.Length + (value?.Length ?? 0) + ParamVariablePrefix.Length + 32;
        if (payload > MaxLaunchPayloadChars)
        {
            throw new InvalidOperationException(
                $"传给脚本的参数过长（约 {payload} 字符，上限 {MaxLaunchPayloadChars}），" +
                "已拒绝启动；请缩短输入后重试。");
        }

        psi.Environment[ScriptPathVariable] = fullPath;
        psi.Environment[ScriptHashVariable] = hash;
        psi.Environment[ParamCountVariable] = parameters.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < parameters.Count; i++)
        {
            var prefix = ParamVariablePrefix + i.ToString(CultureInfo.InvariantCulture);
            psi.Environment[prefix + ParamNameSuffix] = parameters[i].Name;
            if (parameters[i].Value is { } value)
                psi.Environment[prefix + ParamValueSuffix] = value;
            else
                psi.Environment[prefix + ParamFlagSuffix] = "1";
        }
    }

    /// <summary>
    /// 把 PowerShell 具名参数序列拆成（名字, 值 | 开关）：
    /// 只接受 "-Name value" 与 "-Switch" 两种形式，值与名字原样走环境变量。
    /// 位置参数无法表达为具名绑定，宁可明确报错，也不能静默丢掉调用方给的参数。
    /// </summary>
    internal static List<(string Name, string? Value)> ParseArguments(string[] args)
    {
        var parameters = new List<(string Name, string? Value)>();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i] ?? string.Empty;
            if (!IsParameterName(token))
            {
                throw new InvalidOperationException(
                    "不支持的脚本参数形式（只接受 -Name value 或 -Switch）：" + token);
            }

            if (i + 1 < args.Length && !IsParameterName(args[i + 1] ?? string.Empty))
                parameters.Add((token.TrimStart('-'), args[++i] ?? string.Empty));
            else
                parameters.Add((token.TrimStart('-'), null));
        }
        return parameters;
    }

    /// <summary>
    /// 参数名形如 -Name：破折号后必须以字母或下划线开头，
    /// 这样 -1.5 这类负数值不会被误判成开关名。
    /// </summary>
    private static bool IsParameterName(string token)
        => token.Length >= 2
           && token[0] == '-'
           && (char.IsLetter(token[1]) || token[1] == '_');

    /// <summary>只清理本工具自己写下的失败输出，不动该目录里的其他文件。</summary>
    private static void CleanupStaleOutput(string tempDir)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(tempDir, "*.txt"))
            {
                if (File.GetLastWriteTime(stale) < DateTime.Now - TimeSpan.FromDays(1))
                    File.Delete(stale);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }

    /// <summary>把输出里出现的敏感路径替换成占位符（脚本路径、临时目录等）。</summary>
    internal static string SanitizeProcessText(string text, params string[] sensitivePaths)
    {
        var safe = text ?? string.Empty;
        foreach (var path in sensitivePaths
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .SelectMany(p => new[] { p, Path.GetFullPath(p) })
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            safe = safe.Replace(path, "<redacted-path>", StringComparison.OrdinalIgnoreCase);
        }
        return safe;
    }

    internal static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or ',' or '+'))
            return value;
        return "'" + value.Replace("'", "''") + "'";
    }
}
