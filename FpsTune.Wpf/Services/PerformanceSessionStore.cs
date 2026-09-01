using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 性能会话本地持久化：%LOCALAPPDATA%\FpsTune\sessions\&lt;id&gt;.json。
/// - 原子写入（AtomicFile）；
/// - 损坏文件留档 .corrupt 并跳过，不阻塞其余会话；
/// - schemaVersion 不兼容时返回明确错误，不删除用户数据；
/// - 保留上限 MaxSessions，超出删除最旧（仅限本目录、仅限符合命名规则的文件）；
/// - 运行中会话周期性快照到 _active.json，进程异常退出后下次启动可恢复。
/// </summary>
public static class PerformanceSessionStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxSessions = 30;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    internal static string? OverrideDir { get; set; }

    public static string SessionsDir => OverrideDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune", "sessions");

    public static string ActiveSessionPath => Path.Combine(SessionsDir, "_active.json");

    public static void Save(PerformanceSession session)
    {
        Directory.CreateDirectory(SessionsDir);
        var path = Path.Combine(SessionsDir, FileNameFor(session.Id));
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(session, JsonOpts), new UTF8Encoding(false));
    }

    /// <summary>加载全部会话（按开始时间倒序）。损坏文件留档后跳过。</summary>
    public static List<PerformanceSession> LoadAll()
    {
        var result = new List<PerformanceSession>();
        List<string>? files = null;
        try
        {
            if (!Directory.Exists(SessionsDir))
                return result;
            files = Directory.GetFiles(SessionsDir, "*.json").ToList();
        }
        catch
        {
            return result;
        }

        foreach (var file in files)
        {
            if (IsManagedSessionFile(file) && TryLoadFile(file, out var session, out _) && session is not null)
                result.Add(session);
        }
        return result
            .OrderByDescending(s => s.StartedAt)
            .ToList();
    }

    /// <summary>读取单个会话文件。schemaVersion 不兼容时 error 说明且不删除；损坏时留档 .corrupt。</summary>
    public static bool TryLoadFile(string path, out PerformanceSession? session, out string? error)
    {
        session = null;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "文件不存在";
                return false;
            }
            var json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<PerformanceSession>(json, JsonOpts);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Id))
            {
                error = "会话数据不完整";
                return false;
            }
            if (parsed.SchemaVersion > CurrentSchemaVersion)
            {
                error = $"会话格式版本过新（v{parsed.SchemaVersion}，当前支持 v{CurrentSchemaVersion}）";
                return false;
            }
            session = parsed;
            return true;
        }
        catch (Exception ex)
        {
            AtomicFile.PreserveCorrupt(path);
            error = "会话文件损坏，已留档 .corrupt：" + ex.Message;
            return false;
        }
    }

    /// <summary>仅删除指定 id 的会话文件；id 必须是合法会话标识，绝不递归清理目录。</summary>
    public static bool Delete(string id)
    {
        if (!IsValidSessionId(id))
            return false;
        try
        {
            var path = Path.Combine(SessionsDir, FileNameFor(id));
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>保留最近 max 个会话，删除更旧的；只处理本目录下符合命名规则的会话文件。</summary>
    public static void EnforceRetention(int max = MaxSessions)
    {
        try
        {
            if (!Directory.Exists(SessionsDir))
                return;
            var sessions = LoadAll();
            foreach (var old in sessions.Skip(max))
                Delete(old.Id);
        }
        catch
        {
            // 保留策略失败不影响主流程
        }
    }

    // ---------- 运行中快照（进程异常退出后的恢复） ----------

    public static void SaveActiveSnapshot(PerformanceSession partial)
    {
        try
        {
            Directory.CreateDirectory(SessionsDir);
            AtomicFile.WriteAllText(ActiveSessionPath, JsonSerializer.Serialize(partial, JsonOpts), new UTF8Encoding(false));
        }
        catch
        {
            // 快照失败不中断采样
        }
    }

    public static PerformanceSession? LoadActiveSnapshot()
    {
        try
        {
            if (!File.Exists(ActiveSessionPath))
                return null;
            var parsed = JsonSerializer.Deserialize<PerformanceSession>(File.ReadAllText(ActiveSessionPath, Encoding.UTF8), JsonOpts);
            return parsed is not null && string.IsNullOrWhiteSpace(parsed.Id) == false ? parsed : null;
        }
        catch
        {
            AtomicFile.PreserveCorrupt(ActiveSessionPath);
            return null;
        }
    }

    public static void ClearActiveSnapshot()
    {
        try
        {
            if (File.Exists(ActiveSessionPath))
                File.Delete(ActiveSessionPath);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 恢复上次异常退出的会话：快照样本数 ≥ minSamples 时转正保存并返回，否则直接丢弃。
    /// </summary>
    public static PerformanceSession? RecoverInterruptedSession(int minSamples = 5)
    {
        var snapshot = LoadActiveSnapshot();
        if (snapshot is null)
            return null;
        ClearActiveSnapshot();
        if (snapshot.Samples.Count < minSamples)
            return null;

        var recovered = snapshot with
        {
            Id = NewSessionId(),
            Name = string.IsNullOrWhiteSpace(snapshot.Name) ? "（上次中断的会话）" : snapshot.Name + "（中断恢复）",
            EndedAt = snapshot.Samples.Count > 0 ? snapshot.Samples[^1].T : snapshot.StartedAt
        };
        try
        {
            Save(recovered);
        }
        catch
        {
            return null;
        }
        return recovered;
    }

    // ---------- 标识与文件名 ----------

    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    public static string FileNameFor(string id) => id + ".json";

    public static bool IsValidSessionId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.All(c => Uri.IsHexDigit(c))
           && id.Length is 32;

    /// <summary>只认 <32位hex>.json 的会话文件；_active.json、.corrupt 等不参与保留策略。</summary>
    public static bool IsManagedSessionFile(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsValidSessionId(name[..^5]);
    }
}
