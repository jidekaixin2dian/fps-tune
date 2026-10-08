using System.Diagnostics;
using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>一次会话的 FPS 汇总（PresentMon 解析产物）。纯数据，随会话持久化。</summary>
public sealed record SessionFpsStats(int Samples, double AvgFps, double P1Low, double P99Ms, int Stutters);

/// <summary>
/// 0.2.0 C-E：会话 FPS 采集器。会话开始时拉起 PresentMon（stdout 流），结束时终止并解析。
/// 三个降级原因均以资源键记录（UI 侧 Str.T 明示，不静默失效）：
/// 未找到 PresentMon / 未检测到游戏进程 / 采样解析失败。
/// </summary>
public sealed class SessionFpsRecorder
{
    private readonly Func<string?> _findPresentMon;
    private readonly Func<string?, Process?> _findGameProcess;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly Func<Process, Task<string[]>>? _readOutput;
    private readonly Action<Process> _kill;
    private readonly bool _verifyExecutable;

    private Process? _proc;
    private Task<string[]>? _readTask;
    private Task? _captureTask;
    private Task? _errorTask;
    private string? _outputPath;
    private string? _unavailableReasonKey;
    private string? _parseError;

    public SessionFpsRecorder(
        Func<string?>? findPresentMon = null,
        Func<string?, Process?>? findGameProcess = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<Process, Task<string[]>>? readOutput = null,
        Action<Process>? kill = null)
    {
        _findPresentMon = findPresentMon ?? ExperimentRunner.ProbePresentMon;
        _findGameProcess = findGameProcess ?? (path =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var name = Path.GetFileNameWithoutExtension(path);
            var procs = Process.GetProcessesByName(name);
            foreach (var process in procs.Skip(1)) process.Dispose();
            return procs.FirstOrDefault();
        });
        _start = startProcess ?? Process.Start;
        _verifyExecutable = startProcess is null;
        _readOutput = readOutput;
        _kill = kill ?? (p => { try { p.Kill(entireProcessTree: true); } catch { /* 已退出即达标 */ } });
    }

    /// <summary>不可用原因的资源键（Start 失败或 Stop 解析失败时非空）。</summary>
    public string? UnavailableReasonKey => _unavailableReasonKey;
    /// <summary>解析失败的原始细节（随 FpsNote 落库，供诊断）。</summary>
    public string? ParseError => _parseError;

    /// <summary>会话开始时调用。成功后 PresentMon 在后台持续采样；失败时记录降级原因并返回 false。</summary>
    public bool Start(string? gamePath)
    {
        if (_proc is not null) return false;
        _unavailableReasonKey = null;
        _parseError = null;
        var pm = _findPresentMon();
        if (pm is null)
        {
            _unavailableReasonKey = "Str.FpsReasonNoPresentMon";
            return false;
        }
        using var game = _findGameProcess(gamePath);
        if (game is null)
        {
            _unavailableReasonKey = "Str.FpsReasonNoGameProcess";
            return false;
        }
        // PresentMon 要的是进程名：优先取游戏路径的文件名（真实 Process 的 ProcessName
        // 在测试替身/极端时机下可能取不到），两者都没有时兜底。
        var procName = !string.IsNullOrWhiteSpace(gamePath)
            ? Path.GetFileNameWithoutExtension(gamePath)
            : SafeProcessName(game);

        try
        {
            // 不带 --timed：持续输出直到 Stop 终止。stdout 由后台任务持续排空，
            // 防止长会话把管道缓冲写满导致 PresentMon 阻塞。
            var psi = new ProcessStartInfo
            {
                FileName = pm,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "--session_name", "FpsTune-Session", "--process_name", procName, "--no_console_stats", "--output_stdout" })
                psi.ArgumentList.Add(a);
            using var verified = _verifyExecutable ? TrustedCaptureTool.OpenVerified(pm) : null;
            _proc = _start(psi);
            if (_proc is null)
            {
                _unavailableReasonKey = "Str.FpsReasonNoPresentMon";
                return false;
            }
            if (_readOutput is not null)
                _readTask = _readOutput(_proc);
            else
            {
                _outputPath = Path.Combine(Path.GetTempPath(), "FpsTune-frames-" + Guid.NewGuid().ToString("N") + ".csv");
                _captureTask = CaptureOutputAsync(_proc, _outputPath);
                _errorTask = DrainErrorsAsync(_proc);
            }
            return true;
        }
        catch
        {
            _unavailableReasonKey = "Str.FpsReasonNoPresentMon";
            if (_proc is not null) _kill(_proc);
            _proc?.Dispose();
            _proc = null;
            CleanupOutput(_outputPath, _captureTask);
            return false;
        }
    }

    /// <summary>会话结束时调用：终止 PresentMon 并解析全程帧时间。未启动过返回 null。</summary>
    public SessionFpsStats? Stop()
    {
        var proc = _proc;
        var readTask = _readTask;
        var capture = _captureTask;
        var errors = _errorTask;
        var path = _outputPath;
        _proc = null;
        _readTask = null;
        _captureTask = null;
        _errorTask = null;
        _outputPath = null;
        if (proc is null)
            return null;

        FrameParseResult parsed;
        try
        {
            _kill(proc);
            if (readTask is not null)
                parsed = FrameTimeStats.Parse(readTask.Wait(TimeSpan.FromSeconds(10)) ? readTask.Result : []);
            else if (capture is not null && path is not null
                && Task.WhenAll(capture, errors ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(10)))
                parsed = FrameTimeStats.Parse(File.ReadLines(path));
            else
                parsed = FrameTimeStats.Parse([]);
        }
        catch
        {
            parsed = FrameTimeStats.Parse([]);
        }
        finally
        {
            proc.Dispose();
            CleanupOutput(path, capture);
        }

        if (parsed.Frame is null)
        {
            _unavailableReasonKey = "Str.FpsReasonParseFailed";
            _parseError = parsed.Error switch
            {
                FrameParseError.TooFewFrames => $"frames={parsed.FrameCount}",
                FrameParseError.UnknownColumns => "header=" + string.Join(";", parsed.Header),
                FrameParseError.TooFewLines => "lines<2",
                _ => parsed.Error?.ToString() ?? "unknown",
            };
            return null;
        }
        var frame = parsed.Frame;
        return new SessionFpsStats(frame.Samples, frame.AvgFps, frame.P1Low, frame.P99Ms, frame.Stutters);
    }

    private static async Task CaptureOutputAsync(Process process, string path)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 65536, useAsync: true);
        await using var writer = new StreamWriter(file);
        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        var buffer = new char[4096];
        while (await process.StandardError.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
    }

    private static void CleanupOutput(string? path, Task? capture)
    {
        if (path is null) return;
        if (capture is { IsCompleted: false })
        {
            _ = capture.ContinueWith(_ => CleanupOutput(path, null), TaskScheduler.Default);
            return;
        }
        try { File.Delete(path); }
        catch (IOException) { /* Only this recorder's scratch file; never delete user data. */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string SafeProcessName(Process game)
    {
        try
        {
            return game.ProcessName;
        }
        catch
        {
            return "game";
        }
    }
}
