using System.Diagnostics;
using System.IO;
using System.Text;
using System.Globalization;

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
    internal const long MaxOutputBytes = 64L * 1024 * 1024;
    internal const int MaxOutputLines = FrameTimeStats.MaxFrameCount + 1;
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
    private FileStream? _outputFile;
    private int _limitReached;
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
            try
            {
                // 进程名命中后还要求完整路径与档案一致：改名冒充的进程不允许进入会话采样。
                foreach (var process in procs)
                {
                    if (ProcessIdentity.MatchesPath(process, path))
                        return process;
                    process.Dispose();
                }
                return null;
            }
            catch
            {
                foreach (var process in procs)
                    process.Dispose();
                return null;
            }
        });
        _start = startProcess ?? Process.Start;
        _verifyExecutable = startProcess is null;
        _readOutput = readOutput;
        _kill = kill ?? (p => { try { p.Kill(entireProcessTree: true); } catch { /* 已退出即达标 */ } });
    }

    /// <summary>不可用原因的资源键（Start 失败或 Stop 解析失败时非空）。</summary>
    public string? UnavailableReasonKey => Volatile.Read(ref _limitReached) != 0 ? "Str.FpsReasonLimit" : Volatile.Read(ref _unavailableReasonKey);
    /// <summary>解析失败的原始细节（随 FpsNote 落库，供诊断）。</summary>
    public string? ParseError => _parseError;

    /// <summary>会话开始时调用。成功后 PresentMon 在后台持续采样；失败时记录降级原因并返回 false。</summary>
    public bool Start(string? gamePath)
    {
        if (_proc is not null) return false;
        _unavailableReasonKey = null;
        _parseError = null;
        _limitReached = 0;
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
        // 目标进程退出瞬间 Id 可能取不到：没有可靠 PID 就不启动采样，绝不回退按名捕获。
        int gameId;
        try { gameId = game.Id; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _unavailableReasonKey = "Str.FpsReasonNoGameProcess";
            return false;
        }

        try
        {
            // 不带 --timed：持续输出直到 Stop 终止。stdout 由后台任务持续排空，
            // 防止长会话把管道缓冲写满导致 PresentMon 阻塞。
            // game 是通过完整路径身份校验的具体进程：按 PID 捕获，与真游戏同名的改名进程不会进入采样。
            var psi = new ProcessStartInfo
            {
                FileName = pm,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "--session_name", "FpsTune-Session", "--process_id", gameId.ToString(), "--no_console_stats", "--output_stdout" })
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
                var process = _proc;
                // The OS deletes this owned scratch file when its last handle closes, including a crash.
                _outputFile = new FileStream(Path.Combine(Path.GetTempPath(), "FpsTune-frames-" + Guid.NewGuid().ToString("N") + ".csv"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                var output = _outputFile;
                _captureTask = Task.Run(async () =>
                {
                    try
                    {
                        await CaptureOutputAsync(process.StandardOutput, output, MaxOutputBytes, MaxOutputLines, () =>
                        {
                            Interlocked.Exchange(ref _limitReached, 1);
                            _kill(process);
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                        _unavailableReasonKey = "Str.FpsReasonParseFailed";
                        _parseError = "capture-output";
                        _kill(process);
                        throw;
                    }
                });
                _errorTask = DrainErrorsAsync(process);
            }
            return true;
        }
        catch
        {
            _unavailableReasonKey = "Str.FpsReasonNoPresentMon";
            if (_proc is not null) _kill(_proc);
            _proc?.Dispose();
            _proc = null;
            CleanupOutput(_outputFile, _captureTask);
            _outputFile = null;
            return false;
        }
    }

    /// <summary>会话结束时调用：终止 PresentMon 并解析全程帧时间。未启动过返回 null。</summary>
    public SessionFpsStats? Stop() => Stop(parse: true);

    internal SessionFpsStats? Stop(bool parse)
    {
        var proc = _proc;
        var readTask = _readTask;
        var capture = _captureTask;
        var errors = _errorTask;
        var file = _outputFile;
        _proc = null;
        _readTask = null;
        _captureTask = null;
        _errorTask = null;
        _outputFile = null;
        if (proc is null)
            return null;

        FrameParseResult parsed;
        try
        {
            _kill(proc);
            if (!parse) return null;
            if (readTask is not null)
                parsed = FrameTimeStats.Parse(readTask.Wait(TimeSpan.FromSeconds(10)) ? readTask.Result : []);
            else if (capture is not null && file is not null
                && Task.WhenAll(capture, errors ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(10)))
            {
                file.Position = 0;
                using var reader = new StreamReader(file, leaveOpen: true);
                parsed = Volatile.Read(ref _limitReached) != 0 ? FrameTimeStats.Parse([]) : FrameTimeStats.Parse(Lines(reader));
            }
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
            CleanupOutput(file, capture);
        }

        if (Volatile.Read(ref _limitReached) != 0)
        {
            _unavailableReasonKey = "Str.FpsReasonLimit";
            _parseError = "output-limit";
            return null;
        }
        if (parsed.Frame is null)
        {
            _unavailableReasonKey = "Str.FpsReasonParseFailed";
            _parseError ??= parsed.Error switch
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

    internal static async Task CaptureOutputAsync(TextReader input, Stream output, long maxBytes, int maxLines, Action limitReached)
    {
        // Store only round-trip frame times, using the same column/conversion rules as experiments.
        // Unrelated PresentMon columns consume no disk; fixed input chunks bound malformed single lines.
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 65536, leaveOpen: true) { NewLine = "\n" };
        var buffer = new char[4096];
        long bytes = 0;
        var lines = 0;
        var line = new StringBuilder();
        var column = -1;
        var isFps = false;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (!WriteRecord(line.ToString().TrimEnd('\r'))) return;
                    line.Clear();
                }
                else
                {
                    if (line.Length >= FrameTimeStats.MaxCsvLineChars) { limitReached(); return; }
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) WriteRecord(line.ToString().TrimEnd('\r'));

        bool WriteRecord(string record)
        {
            string value;
            if (column < 0)
            {
                var header = FrameTimeStats.ReadHeader(record);
                column = FrameTimeStats.FindColumn(header);
                if (column < 0) throw new InvalidDataException("Unrecognized PresentMon frame column.");
                isFps = header[column].Equals("FPS", StringComparison.OrdinalIgnoreCase);
                value = "FrameTime";
            }
            else
            {
                if (!FrameTimeStats.TryReadFrame(record, column, isFps, out var ms)) return true;
                value = ms.ToString("R", CultureInfo.InvariantCulture);
            }
            if (lines >= maxLines || bytes + value.Length + 1 > maxBytes) { limitReached(); return false; }
            writer.WriteLine(value);
            bytes += value.Length + 1;
            lines++;
            return true;
        }
    }

    private static IEnumerable<string> Lines(TextReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        var buffer = new char[4096];
        while (await process.StandardError.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
    }

    private static void CleanupOutput(FileStream? file, Task? capture)
    {
        if (file is null) return;
        if (capture is { IsCompleted: false })
        {
            _ = capture.ContinueWith(_ => CleanupOutput(file, null), TaskScheduler.Default);
            return;
        }
        try { file.Dispose(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
