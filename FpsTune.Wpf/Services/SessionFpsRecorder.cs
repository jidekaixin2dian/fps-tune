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
    private readonly Func<Process, Task<string[]>> _readOutput;
    private readonly Action<Process> _kill;

    private Process? _proc;
    private Task<string[]>? _readTask;
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
            return procs.Length > 0 ? procs[0] : null;
        });
        _start = startProcess ?? Process.Start;
        _readOutput = readOutput ?? (Func<Process, Task<string[]>>)(async p =>
        {
            // 与 ExperimentRunner.RunProcessAsync 同口径：按换行切行
            var text = await p.StandardOutput.ReadToEndAsync();
            return text.Split('\n');
        });
        _kill = kill ?? (p => { try { p.Kill(entireProcessTree: true); } catch { /* 已退出即达标 */ } });
    }

    /// <summary>不可用原因的资源键（Start 失败或 Stop 解析失败时非空）。</summary>
    public string? UnavailableReasonKey => _unavailableReasonKey;
    /// <summary>解析失败的原始细节（随 FpsNote 落库，供诊断）。</summary>
    public string? ParseError => _parseError;

    /// <summary>会话开始时调用。成功后 PresentMon 在后台持续采样；失败时记录降级原因并返回 false。</summary>
    public bool Start(string? gamePath)
    {
        var pm = _findPresentMon();
        if (pm is null)
        {
            _unavailableReasonKey = "Str.FpsReasonNoPresentMon";
            return false;
        }
        var game = _findGameProcess(gamePath);
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
            _proc = _start(psi);
            if (_proc is null)
            {
                _unavailableReasonKey = "Str.FpsReasonNoPresentMon";
                return false;
            }
            _readTask = _readOutput(_proc);
            return true;
        }
        catch
        {
            _unavailableReasonKey = "Str.FpsReasonNoPresentMon";
            _proc?.Dispose();
            _proc = null;
            return false;
        }
    }

    /// <summary>会话结束时调用：终止 PresentMon 并解析全程帧时间。未启动过返回 null。</summary>
    public SessionFpsStats? Stop()
    {
        var proc = _proc;
        var readTask = _readTask;
        _proc = null;
        _readTask = null;
        if (proc is null || readTask is null)
            return null;

        string[] lines;
        try
        {
            _kill(proc);
            lines = readTask.Wait(TimeSpan.FromSeconds(10)) ? readTask.Result : [];
        }
        catch
        {
            lines = [];
        }
        finally
        {
            proc.Dispose();
        }

        var parsed = FrameTimeStats.Parse(lines.Where(l => l.Length > 0));
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
