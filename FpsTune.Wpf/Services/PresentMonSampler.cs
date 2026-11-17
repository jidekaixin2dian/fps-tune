using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FpsTune.Wpf.Services;

/// <summary>
/// PresentMon 定点采样执行：先探测采样器版本选参数族，再在<b>一次总预算</b>内按回退顺序尝试，
/// 最后把 stdout 或输出文件收敛成 csvOut。实验管线与 CaptureOrchestrator 共用这一条执行路径。
///
/// 为什么不是「六条参数链一路试到底」：0.5.0 的真机实测里，采不到帧时旧实现会把
/// v2 四条 + v1 两条依次跑完（每条超时 = 采样秒数 + 30），单次 <c>-Capture</c> 要 9 分钟才报错。
/// 现在先跑一次 <c>--help</c>（毫秒级）判断参数族，再给整次采样一个 <c>秒数 + 45</c> 的总预算。
/// </summary>
internal static class PresentMonSampler
{
    /// <summary>
    /// 采样结果：成败 + 失败原因（<see cref="CaptureGuidance"/> 里的 ReasonCode，null = 普通无输出）
    /// + 本次 PresentMon 报告的丢事件条数（「事件在产生但一帧都没拿到」的唯一客观线索）。
    /// </summary>
    internal sealed record CaptureOutcome(bool Sampled, string? FailureReason = null, long LostEvents = 0)
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

    /// <summary>PresentMon 丢事件的告警形如 <c>warning: 56414 ETW events were lost.</c>。</summary>
    private static readonly Regex LostEventsPattern = new(@"(\d+)\s+ETW events were lost", RegexOptions.IgnoreCase);

    /// <summary>采样窗口之外的固定预算：进程启动、ETW 会话收尾、CSV 落盘。</summary>
    private const int NonSamplingBudgetSeconds = 45;

    /// <summary>采样器参数族：v2 用 <c>--timed</c>，v1（如 FrameView 自带 1.9）用 <c>--duration</c>。</summary>
    internal enum ArgsFamily { V2, V1 }

    /// <summary>
    /// 从 <c>--help</c> 文本判断参数族：明确没有 <c>--timed</c> 才按 v1（FrameView 1.x）处理；
    /// 探测失败（null）按 v2 处理——内置采样器就是 v2，宁可先试现代参数也不要误走 v1。
    /// </summary>
    internal static ArgsFamily FamilyFromHelp(string? helpText)
        => string.IsNullOrWhiteSpace(helpText) || helpText.Contains("--timed", StringComparison.OrdinalIgnoreCase)
            ? ArgsFamily.V2
            : ArgsFamily.V1;

    private static readonly ConcurrentDictionary<string, ArgsFamily> FamilyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>测试注入：替换 <c>--help</c> 探测（返回帮助文本，null = 探测失败）。</summary>
    internal static Func<string, string?>? HelpProbeOverride;

    /// <summary>
    /// 采样一次，落盘 csvOut。总预算 = <paramref name="seconds"/> + 45 秒；
    /// PresentMon 明确报「无法启动 trace 会话 / 拒绝访问」时按权限不足分类（需要管理员或 Performance Log Users 组）。
    /// </summary>
    internal static async Task<CaptureOutcome> CaptureAsync(
        string presentMon, int? pid, string processName, int seconds, string csvOut, CancellationToken ct)
    {
        var budget = TimeSpan.FromSeconds(seconds + NonSamplingBudgetSeconds);
        var clock = Stopwatch.StartNew();

        var family = await DetectFamilyAsync(presentMon, ct).ConfigureAwait(false);
        var attempts = BuildAttempts(family, pid, processName, seconds, csvOut);

        var permissionDenied = false;
        long lostEvents = 0;
        foreach (var args in attempts)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = budget - clock.Elapsed;
            if (remaining <= TimeSpan.FromSeconds(2))
                break;                      // 预算已用尽：不再起新的尝试，交给调用方给出失败原因
            if (File.Exists(csvOut))
                File.Delete(csvOut);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(remaining);
            var (code, output, errors) = await RunProcessAsync(presentMon, args, timeout.Token).ConfigureAwait(false);
            lostEvents = Math.Max(lostEvents, MaxLostEvents(errors.Concat(output)));
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
        if (permissionDenied)
            return new CaptureOutcome(false, CaptureGuidance.NoPermission);
        // 退出码 0、会话跑完、却一直在丢事件：事件确实在产生，只是进不到采样器。
        // 这是「被系统或反作弊拦下」的客观证据，不能和「目标进程没在渲染」混成同一句话。
        return lostEvents > 0
            ? new CaptureOutcome(false, CaptureGuidance.EventsLost, lostEvents)
            : new CaptureOutcome(false);
    }

    /// <summary>从 PresentMon 输出里取最大的一处「丢了多少条 ETW 事件」；没有这类告警则 0。</summary>
    internal static long MaxLostEvents(IEnumerable<string> lines)
    {
        long max = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;
            foreach (Match match in LostEventsPattern.Matches(line))
            {
                if (long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                    && count > max)
                    max = count;
            }
        }
        return max;
    }

    /// <summary>按参数族与目标生成尝试顺序：先 stdout（由本进程落盘），再落文件。</summary>
    private static string[][] BuildAttempts(ArgsFamily family, int? pid, string processName, int seconds, string csvOut)
    {
        if (family == ArgsFamily.V1)
        {
            // PresentMon 1.x（例如 FrameView SDK 自带 1.9）只有单横线参数与 --output-file。
            return pid is { } v1Pid
                ? [[ "--process", v1Pid.ToString(), "--duration", seconds.ToString(), "--output-file", csvOut ]]
                : [[ "--process-name", processName, "--duration", seconds.ToString(), "--output-file", csvOut ]];
        }

        // v2：独立 session 名，避免与 NVIDIA FrameView 服务已启动的默认 "PresentMon" 会话冲突。
        var head = new[] { "--session_name", "FpsTune", "--timed", seconds.ToString(), "--terminate_after_timed", "--no_console_stats" };
        return pid is { } processId
            ?
            [
                [.. head, "--process_id", processId.ToString(), "--output_stdout"],
                [.. head, "--process_name", processName, "--output_stdout"],
                [.. head, "--process_id", processId.ToString(), "--output_file", csvOut],
            ]
            :
            [
                [.. head, "--process_name", processName, "--output_stdout"],
                [.. head, "--process_name", processName, "--output_file", csvOut],
            ];
    }

    private static async Task<ArgsFamily> DetectFamilyAsync(string exe, CancellationToken ct)
    {
        if (FamilyCache.TryGetValue(exe, out var cached))
            return cached;

        string? help = null;
        try
        {
            help = HelpProbeOverride is { } probe
                ? probe(exe)
                : await Task.Run(() => RunHelp(exe), ct).ConfigureAwait(false);
        }
        catch
        {
            // 探测失败按 v2 处理（内置采样器就是 v2），不影响正式采样
        }
        var family = FamilyFromHelp(help);
        FamilyCache[exe] = family;
        return family;
    }

    private static string? RunHelp(string exe)
    {
        try
        {
            using var verified = TrustedCaptureTool.OpenVerified(exe);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--help");
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            var read = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return read.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
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
