using System.Diagnostics;
using System.Text;
using System.IO;

namespace FpsTune.Wpf.Services;

public sealed record RunResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

public static class PowerShellRunner
{
    public static async Task<RunResult> RunAsync(
        string scriptPath, string[] args, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "delta-tune-wpf-tmp");
        Directory.CreateDirectory(tempDir);

        // 顺手清理历史残留（超过 1 天的旧临时文件）。
        try
        {
            foreach (var stale in Directory.EnumerateFiles(tempDir))
            {
                if (File.GetLastWriteTime(stale) < DateTime.Now - TimeSpan.FromDays(1))
                    File.Delete(stale);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }

        var name = $"{Path.GetFileNameWithoutExtension(scriptPath)}_{Guid.NewGuid():N}";
        var wrapper = Path.Combine(tempDir, name + ".ps1");
        var stdout = Path.Combine(tempDir, name + ".out.txt");
        var stderr = Path.Combine(tempDir, name + ".err.txt");

        var sb = new StringBuilder();
        sb.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
        sb.Append("& ").Append(Quote(scriptPath));
        foreach (var arg in args)
        {
            sb.Append(' ').Append(Quote(arg));
        }

        try
        {
            File.WriteAllText(wrapper, sb.ToString(), new UTF8Encoding(true));

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{wrapper}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = root
            };

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

            var safeOutput = SanitizeProcessText(output, scriptPath, wrapper);
            var safeError = SanitizeProcessText(error, scriptPath, wrapper);
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
        finally
        {
            // wrapper 只负责本次启动，成功、失败、取消和启动异常都不应遗留。
            try { File.Delete(wrapper); } catch { }
        }
    }

    internal static string SanitizeProcessText(string text, string scriptPath, string wrapperPath)
    {
        var safe = text ?? string.Empty;
        foreach (var path in new[]
                 {
                     scriptPath,
                     wrapperPath,
                     Path.GetFullPath(scriptPath),
                     Path.GetFullPath(wrapperPath)
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
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
