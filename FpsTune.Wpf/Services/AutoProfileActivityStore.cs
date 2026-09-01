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

    internal static string? OverrideDir { get; set; }

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
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EventsFile)!);
                File.AppendAllText(EventsFile, JsonSerializer.Serialize(e, LineOpts) + "\n", new UTF8Encoding(false));
            }
        }
        catch
        {
            // 审计写入失败不影响自动应用主流程
        }
    }

    /// <summary>最近事件（新→旧），最多 MaxEvents 条；尾部多余行会被截断回收。</summary>
    public static List<AutoProfileEvent> Load()
    {
        List<string> lines;
        try
        {
            lock (Gate)
            {
                if (!File.Exists(EventsFile))
                    return [];
                lines = File.ReadAllLines(EventsFile, Encoding.UTF8).ToList();
            }
        }
        catch
        {
            return [];
        }

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
        result.Reverse();
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
