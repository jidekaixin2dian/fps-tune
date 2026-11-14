using System.Diagnostics;
using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>
/// PresentMon 定点采样执行：参数回退链（v2 stdout → v2 文件 → v1 文件）、超时、stdout 落盘。
/// 0.5.0 从 ExperimentRunner 抽出——实验管线与 CaptureOrchestrator 走同一条执行路径，
/// 参数组合与超时语义逐字保留。
/// </summary>
internal static class PresentMonSampler
{
    /// <summary>采样结果：成败 + 失败原因（权限不足时给出明确指引，其余按采样失败描述）。</summary>
    internal sealed record CaptureOutcome(bool Sampled, string? FailureReason = null)
    {
        public static implicit operator bool(CaptureOutcome outcome) => outcome.Sampled;
    }

    private static readonly string[] PermissionMarkers =
    [
        "failed to start trace session",
        "access denied",
        "requires elevated privilege",
        "administrative privilege",
    ];

    /// <summary>
    /// 采样一次，落盘 csvOut。全部参数组合失败或超时返回失败结果；
    /// PresentMon 明确报「无法启动 trace 会话 / 拒绝访问」时按权限不足分类（需要管理员或 Performance Log Users 组）。
    /// </summary>
    internal static async Task<CaptureOutcome> CaptureAsync(
        string presentMon, int? pid, string processName, int seconds, string csvOut, CancellationToken ct)
    {
        // 先试官方 v2 参数（--timed / --terminate_after_timed），优先 --output_stdout（由本进程落盘，
        // 避免部分版本 --output_file 不落盘），再回退 --output_file 与 v1 参数（--duration）。
        // 使用独立 session 名，避免与 NVIDIA FrameView 服务已启动的默认 "PresentMon" 会话冲突。
        var v2Head = new[] { "--session_name", "FpsTune", "--timed", seconds.ToString(), "--terminate_after_timed", "--no_console_stats" };
        var attempts = pid is { } processId
            ? new[]
            {
                v2Head.Concat(["--process_id", processId.ToString(), "--output_stdout"]).ToArray(),
                v2Head.Concat(["--process_name", processName, "--output_stdout"]).ToArray(),
                v2Head.Concat(["--process_id", processId.ToString(), "--output_file", csvOut]).ToArray(),
                v2Head.Concat(["--process_name", processName, "--output_file", csvOut]).ToArray(),
                new[] { "--process", processId.ToString(), "--duration", seconds.ToString(), "--output-file", csvOut },
                new[] { "--process-name", processName, "--duration", seconds.ToString(), "--output-file", csvOut },
            }
            : new[]
            {
                v2Head.Concat(["--process_name", processName, "--output_stdout"]).ToArray(),
                v2Head.Concat(["--process_name", processName, "--output_file", csvOut]).ToArray(),
                new[] { "--process-name", processName, "--duration", seconds.ToString(), "--output-file", csvOut },
            };

        var permissionDenied = false;
        foreach (var args in attempts)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(csvOut))
                File.Delete(csvOut);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 30));
            var (code, output, errors) = await RunProcessAsync(presentMon, args, timeout.Token).ConfigureAwait(false);
            if (code != 0)
            {
                if (errors.Concat(output).Any(line =>
                        PermissionMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase))))
                    permissionDenied = true;
                continue;
            }
            if (args.Contains("--output_stdout"))
            {
                if (output.Length == 0)
                    continue;
                var directory = Path.GetDirectoryName(csvOut);
                if (directory is { Length: > 0 })
                    Directory.CreateDirectory(directory);
                await File.WriteAllLinesAsync(csvOut, output.Where(l => l.Length > 0), ct).ConfigureAwait(false);
                var fi = new FileInfo(csvOut);
                if (fi.Exists && fi.Length > 0)
                    return new CaptureOutcome(true);
            }
            else if (File.Exists(csvOut))
            {
                return new CaptureOutcome(true);
            }
        }
        return permissionDenied
            ? new CaptureOutcome(false, "permission")
            : new CaptureOutcome(false);
    }

    private static async Task<(int Code, string[] Output, string[] Errors)> RunProcessAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var verified = TrustedCaptureTool.OpenVerified(exe);
            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, [], []);
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);
            return (proc.ExitCode, stdout.Split('\n'), stderr.Split('\n'));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (-1, [], []);
        }
    }
}
