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
    public static async Task<RunResult> RunAsync(string scriptPath, params string[] args)
    {
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

        File.WriteAllText(wrapper, sb.ToString(), new UTF8Encoding(true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{wrapper}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = root
        };

        using var process = Process.Start(psi)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        var exitCode = process.ExitCode;

        // 成功时清理全部临时文件；失败时保留输出文件便于排查。
        if (exitCode != 0)
        {
            if (!string.IsNullOrEmpty(output))
                File.WriteAllText(stdout, output, new UTF8Encoding(true));
            if (!string.IsNullOrEmpty(error))
                File.WriteAllText(stderr, error, new UTF8Encoding(true));
        }
        else
        {
            try { File.Delete(wrapper); } catch { }
        }

        return new RunResult(exitCode, output, error);
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
