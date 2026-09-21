using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsTune.Wpf.Services;

/// <summary>自动 Profile 活动事件。Detail 为简洁中文描述，不含完整用户路径。</summary>
public sealed record AutoProfileEvent(
    DateTime Time,
    string Kind,
    string Process,
    string? Profile,
    string Detail);

/// <summary>
/// 自动 Profile 活动中心的本地审计存储（%LOCALAPPDATA%\FpsTune\auto-profile-events.jsonl）。
/// - 只追加，读取时有界（保留最近 MaxEvents 条）；
/// - 单行损坏跳过，不影响其余记录；
/// - 不含遥测，不上传；"清除记录" 删除整个文件。
/// </summary>
public static class AutoProfileActivityStore
{
    public const string KindMatch = "match";
    public const string KindApplied = "applied";
    public const string KindSkipped = "skipped";
    public const string KindFailed = "failed";
    public const string KindInfo = "info";

    public const int MaxEvents = 200;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions LineOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static string? OverrideDir
    {
        get => _overrideDir;
        set
        {
            _overrideDir = value;
            // 换目录等于换文件，行数缓存必须作废，否则会按上一个文件的行数决定是否压缩。
            System.Threading.Interlocked.Exchange(ref _lineCount, -1);
        }
    }

    private static string? _overrideDir;

    /// <summary>当前事件文件的行数缓存；-1 表示未知，需要读一次。</summary>
    private static int _lineCount = -1;

    /// <summary>最后一次轮询扫描时间（内存态，重启后从本轮重新计）。</summary>
    public static DateTime? LastScanAt { get; private set; }

    /// <summary>本轮累计扫描次数（内存态）。</summary>
    public static long ScanCount { get; private set; }

    public static string EventsFile => Path.Combine(
        OverrideDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune"),
        "auto-profile-events.jsonl");

    public static void NoteScan()
    {
        LastScanAt = DateTime.Now;
        ScanCount++;
    }

    public static void Append(AutoProfileEvent e)
    {
        if (e is null)
            return;

        try
        {
            lock (Gate)
            {
                var path = EventsFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // 活动中心只保存短文本；异常消息/用户自定义名称也不能把完整路径写入审计。
                var sanitized = new AutoProfileEvent(
                    e.Time,
                    PrivacyScrub.Sanitize(e.Kind),
                    PrivacyScrub.Sanitize(e.Process),
                    string.IsNullOrWhiteSpace(e.Profile) ? null : PrivacyScrub.Sanitize(e.Profile),
                    PrivacyScrub.Sanitize(e.Detail));
                // 一条事件只追加一行。整文件重写会让每条事件都替换一次文件，既随事件数放大开销，
                // 又和杀软/索引器对新文件的短暂独占句柄相撞（CI 上表现为 IOException）；
                // 物理文件的有界性改由超限时的一次压缩维持。
                File.AppendAllText(path, JsonSerializer.Serialize(sanitized, LineOpts) + "\n", new UTF8Encoding(false));
                if (_lineCount >= 0)
                    _lineCount++;

                if (CountLinesLocked(path) > MaxEvents)
                    CompactLocked(path);
            }
        }
        catch
        {
            // 审计写入失败不影响自动应用主流程
        }
    }

    private static int CountLinesLocked(string path)
    {
        if (_lineCount < 0)
            _lineCount = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).Length : 0;
        return _lineCount;
    }

    /// <summary>把物理文件压回最近 MaxEvents 条（原子替换）。读取失败时保持原文件不动。</summary>
    private static void CompactLocked(string path)
    {
        var events = ReadEventsChronologicalLocked(path);
        var text = string.Join("\n", events.Select(x => JsonSerializer.Serialize(x, LineOpts))) + "\n";
        AtomicFile.WriteAllText(path, text, new UTF8Encoding(false));
        _lineCount = events.Count;
    }

    /// <summary>最近事件（新→旧），最多 MaxEvents 条；尾部多余行会被截断回收。</summary>
    public static List<AutoProfileEvent> Load()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(EventsFile))
                    return [];
                var events = ReadEventsChronologicalLocked(EventsFile);
                events.Reverse();
                return events;
            }
        }
        catch
        {
            return [];
        }
    }

    private static List<AutoProfileEvent> ReadEventsChronologicalLocked(string path)
    {
        // 只保留尾部有限行，避免一个历史损坏/被外部放大的文件拖垮设置页。
        // 用 ReadAllLines 而不是 ReadLines：后者是惰性迭代器，调用方提前退出时句柄要等
        // 终结器才释放，期间任何对该文件的写入都会撞 IOException。
        var lines = File.ReadAllLines(path, Encoding.UTF8).TakeLast(MaxEvents * 2).ToList();
        var result = new List<AutoProfileEvent>();
        foreach (var raw in lines)
        {
            var line = raw.TrimStart('\uFEFF').Trim();
            if (line.Length == 0)
                continue;
            try
            {
                if (JsonSerializer.Deserialize<AutoProfileEvent>(line, LineOpts) is { } e
                    && !string.IsNullOrWhiteSpace(e.Kind))
                    result.Add(e);
            }
            catch
            {
                // 单行损坏跳过
            }
        }

        if (result.Count > MaxEvents)
            result = result.TakeLast(MaxEvents).ToList();
        return result;
    }

    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(EventsFile))
                    File.Delete(EventsFile);
                _lineCount = 0;
            }
        }
        catch
        {
        }
    }

    public static string KindText(string kind) => kind switch
    {
        KindMatch => "匹配",
        KindApplied => "已应用",
        KindSkipped => "跳过",
        KindFailed => "失败",
        _ => "信息"
    };
}
