using System.Globalization;
using System.IO;
using System.Security.Cryptography;
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
    // Persisted sessions must remain readable when a user later enables low-spec
    // mode (whose live buffer is smaller), so this is independent of UI settings.
    public const int MaxSamplesPerSession = 14400;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    internal static string? OverrideDir { get; set; }
    internal static Action<string>? DeleteFileOverride { get; set; }
    internal static Action<string>? WriteActiveTombstoneOverride { get; set; }
    private static readonly object ActiveOwnershipSync = new();
    private static FileStream? ActiveOwner;

    public static string SessionsDir => OverrideDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune", "sessions");

    public static string ActiveSessionPath => Path.Combine(SessionsDir, "_active.json");

    internal static string ActiveLockPath => Path.Combine(SessionsDir, "_active.lock");
    internal static string ActiveCancellationPath => Path.Combine(SessionsDir, "_active.cancelled");

    /// <summary>
    /// 获取运行中会话的跨进程所有权。锁句柄必须保持到会话结束；进程崩溃时由
    /// OS 释放句柄，下一实例即可安全接管恢复。同一进程也只允许一个持有者。
    /// </summary>
    internal static IDisposable? TryAcquireActiveSessionOwnership()
    {
        lock (ActiveOwnershipSync)
        {
            if (ActiveOwner is not null)
                return null;

            FileStream? stream = null;
            try
            {
                Directory.CreateDirectory(SessionsDir);
                var lockPath = ActiveLockPath;
                stream = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                ActiveOwner = stream;
                return new ActiveSessionLease(stream, lockPath);
            }
            catch
            {
                stream?.Dispose();
                return null;
            }
        }
    }

    internal static bool HasActiveSessionOwnership
    {
        get
        {
            lock (ActiveOwnershipSync)
                return ActiveOwner is not null;
        }
    }

    public static void Save(PerformanceSession session)
    {
        EnsureValidSessionForSave(session);
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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "文件不存在";
                return false;
            }
            var json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<PerformanceSession>(json, JsonOpts);
            if (parsed is null)
            {
                error = "会话数据不完整";
                AtomicFile.PreserveCorrupt(path);
                return false;
            }
            if (parsed.SchemaVersion > CurrentSchemaVersion)
            {
                error = $"会话格式版本过新（v{parsed.SchemaVersion}，当前支持 v{CurrentSchemaVersion}）";
                return false;
            }
            if (parsed.SchemaVersion < CurrentSchemaVersion)
            {
                error = $"会话格式版本过旧（v{parsed.SchemaVersion}，当前支持 v{CurrentSchemaVersion}）";
                AtomicFile.PreserveCorrupt(path);
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.Id))
            {
                error = "会话数据不完整";
                AtomicFile.PreserveCorrupt(path);
                return false;
            }
            if (!TryValidateSession(parsed, allowActiveId: false, out error))
            {
                AtomicFile.PreserveCorrupt(path);
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
            max = Math.Max(0, max);
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
        if (HasActiveSessionOwnership)
            return;
        SaveActiveSnapshotCore(partial, requireOwnership: false);
    }

    internal static void SaveActiveSnapshotOwned(PerformanceSession partial)
    {
        SaveActiveSnapshotCore(partial, requireOwnership: true);
    }

    private static void SaveActiveSnapshotCore(PerformanceSession partial, bool requireOwnership)
    {
        try
        {
            if (!TryValidateSession(partial, allowActiveId: true, out _))
                return;
            IDisposable? temporary = null;
            if (!HasActiveSessionOwnership)
            {
                if (requireOwnership || (temporary = TryAcquireActiveSessionOwnership()) is null)
                    return;
            }
            try
            {
                Directory.CreateDirectory(SessionsDir);
                AtomicFile.WriteAllText(ActiveSessionPath, JsonSerializer.Serialize(partial, JsonOpts), new UTF8Encoding(false));
            }
            finally
            {
                temporary?.Dispose();
            }
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
            if (parsed is null)
            {
                AtomicFile.PreserveCorrupt(ActiveSessionPath);
                return null;
            }
            // A future schema must stay untouched so a newer build can recover it;
            // promoting it here would write data the current build cannot interpret.
            if (parsed.SchemaVersion > CurrentSchemaVersion)
                return null;
            if (parsed.SchemaVersion < CurrentSchemaVersion ||
                !TryValidateSession(parsed, allowActiveId: true, out _))
            {
                AtomicFile.PreserveCorrupt(ActiveSessionPath);
                return null;
            }
            return parsed;
        }
        catch
        {
            AtomicFile.PreserveCorrupt(ActiveSessionPath);
            return null;
        }
    }

    /// <summary>
    /// 是否存在"本版本无法解释"的活动快照（来自更新的版本，或已损坏/结构不合法）。
    /// 这类快照会阻塞新会话，但绝不能静默删除，必须由用户明确决定是否归档。
    /// 注意：损坏文件会在读取时按既有约定留档一份 .corrupt。
    /// </summary>
    public static bool HasUnreadableActiveSnapshot()
    {
        try
        {
            if (!File.Exists(ActiveSessionPath))
                return false;
            if (IsFutureSchemaActiveSnapshot())
                return true;
            return LoadActiveSnapshot() is null;
        }
        catch
        {
            return File.Exists(ActiveSessionPath);
        }
    }

    /// <summary>
    /// 用户明确要求后，把无法解释的活动快照改名留档（绝不删除、不参与统计），
    /// 让性能会话恢复可用。可读的快照一律拒绝归档，避免丢掉可恢复数据。
    /// </summary>
    public static bool TryArchiveUnreadableActiveSnapshot(out string? archivedPath, out string? error)
    {
        archivedPath = null;
        error = null;

        var ownership = TryAcquireActiveSessionOwnership();
        if (ownership is null)
        {
            error = "性能会话正在使用中，或另一个 FpsTune 实例持有会话锁。";
            return false;
        }

        using (ownership)
        {
            if (!File.Exists(ActiveSessionPath))
            {
                error = "没有需要归档的活动快照。";
                return false;
            }

            if (!HasUnreadableActiveSnapshot())
            {
                error = "当前活动快照可以被本版本读取，请走正常恢复流程，未执行归档。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(SessionsDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var target = Path.Combine(SessionsDir, $"_active.archived-{stamp}.json");
                for (var i = 1; File.Exists(target) && i < 100; i++)
                    target = Path.Combine(SessionsDir, $"_active.archived-{stamp}-{i}.json");

                File.Move(ActiveSessionPath, target);
                archivedPath = target;
                // 墓碑只对原快照有意义：归档成功后一并清理（清理失败不影响归档结果）。
                ClearActiveCancellationOwned();
                return !File.Exists(ActiveSessionPath);
            }
            catch (Exception ex)
            {
                error = "归档失败：" + (string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
                return false;
            }
        }
    }

    public static bool ClearActiveSnapshot()
        => !HasActiveSessionOwnership && ClearActiveSnapshotCore(requireOwnership: false);

    internal static bool ClearActiveSnapshotOwned()
        => ClearActiveSnapshotCore(requireOwnership: true);

    private static bool ClearActiveSnapshotCore(bool requireOwnership)
    {
        IDisposable? temporary = null;
        try
        {
            if (!HasActiveSessionOwnership)
            {
                if (requireOwnership || (temporary = TryAcquireActiveSessionOwnership()) is null)
                    return false;
            }
            if (!File.Exists(ActiveSessionPath))
                return true;
            if (DeleteFileOverride is { } delete)
                delete(ActiveSessionPath);
            else
                File.Delete(ActiveSessionPath);
            return !File.Exists(ActiveSessionPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    public static bool MarkActiveSnapshotCancelled()
        => !HasActiveSessionOwnership && MarkActiveSnapshotCancelledCore(requireOwnership: false, marker: "cancelled");

    internal static bool MarkActiveSnapshotCancelledOwned()
        => MarkActiveSnapshotCancelledCore(requireOwnership: true, marker: "cancelled");

    internal static bool MarkActiveSnapshotHandledOwned()
        => MarkActiveSnapshotCancelledCore(requireOwnership: true, marker: "handled");

    private static bool MarkActiveSnapshotCancelledCore(bool requireOwnership, string marker)
    {
        IDisposable? temporary = null;
        try
        {
            if (!HasActiveSessionOwnership)
            {
                if (requireOwnership || (temporary = TryAcquireActiveSessionOwnership()) is null)
                    return false;
            }
            Directory.CreateDirectory(SessionsDir);
            if (WriteActiveTombstoneOverride is { } write)
                write(ActiveCancellationPath);
            else
                AtomicFile.WriteAllTextDurable(
                    ActiveCancellationPath,
                    marker + "\n",
                    new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    public static bool ClearActiveCancellation()
        => !HasActiveSessionOwnership && ClearActiveCancellationCore(requireOwnership: false);

    internal static bool ClearActiveCancellationOwned()
        => ClearActiveCancellationCore(requireOwnership: true);

    private static bool ClearActiveCancellationCore(bool requireOwnership)
    {
        IDisposable? temporary = null;
        try
        {
            if (!HasActiveSessionOwnership)
            {
                if (requireOwnership || (temporary = TryAcquireActiveSessionOwnership()) is null)
                    return false;
            }
            if (!File.Exists(ActiveCancellationPath))
                return true;
            File.Delete(ActiveCancellationPath);
            return !File.Exists(ActiveCancellationPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    internal static bool HasActiveSnapshot => File.Exists(ActiveSessionPath);
    internal static bool HasActiveCancellation => File.Exists(ActiveCancellationPath);

    private enum ActiveTombstoneKind
    {
        None,
        Cancelled,
        Handled,
        Unknown
    }

    private static ActiveTombstoneKind ReadActiveTombstone()
    {
        try
        {
            if (!File.Exists(ActiveCancellationPath))
                return ActiveTombstoneKind.None;

            return File.ReadAllText(ActiveCancellationPath, Encoding.UTF8) switch
            {
                "cancelled" or "cancelled\n" or "cancelled\r\n" => ActiveTombstoneKind.Cancelled,
                "handled" or "handled\n" or "handled\r\n" => ActiveTombstoneKind.Handled,
                _ => ActiveTombstoneKind.Unknown
            };
        }
        catch
        {
            // An unreadable marker is not evidence that the active data may be
            // discarded. Keep it and let the next launch retry/quarantine it.
            return ActiveTombstoneKind.Unknown;
        }
    }

    private static bool IsFutureSchemaActiveSnapshot()
    {
        try
        {
            if (!File.Exists(ActiveSessionPath))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(ActiveSessionPath, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!document.RootElement.TryGetProperty("SchemaVersion", out var schema) &&
                !document.RootElement.TryGetProperty("schemaVersion", out schema))
                return false;
            return schema.ValueKind == JsonValueKind.Number &&
                schema.TryGetInt32(out var version) &&
                version > CurrentSchemaVersion;
        }
        catch
        {
            return false;
        }
    }

    private static bool QuarantineUnknownActiveTombstoneOwned()
    {
        try
        {
            if (!File.Exists(ActiveCancellationPath))
                return true;
            File.Move(ActiveCancellationPath, ActiveCancellationPath + ".corrupt", overwrite: true);
            return !File.Exists(ActiveCancellationPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 新会话开始前清理上次取消但未能删除的快照；没有取消标记的 active
    /// 必须保留给启动恢复流程，不能被新会话覆盖。
    /// </summary>
    internal static bool PrepareForNewSession()
    {
        if (!HasActiveSessionOwnership)
            return false;
        var tombstone = ReadActiveTombstone();
        if (tombstone == ActiveTombstoneKind.Unknown)
        {
            if (!QuarantineUnknownActiveTombstoneOwned())
                return false;
            return !HasActiveSnapshot;
        }
        if (tombstone is ActiveTombstoneKind.Cancelled or ActiveTombstoneKind.Handled)
        {
            // A current build must never delete data written by a newer schema,
            // even when an older cancellation/handled marker is beside it.
            if (IsFutureSchemaActiveSnapshot())
                return false;
            if (!ClearActiveSnapshotOwned() || !ClearActiveCancellationOwned())
                return false;
        }
        return !HasActiveSnapshot;
    }

    /// <summary>
    /// 恢复上次异常退出的会话：快照样本数 ≥ minSamples 时转正保存并返回，否则直接丢弃。
    /// </summary>
    public static PerformanceSession? RecoverInterruptedSession(int minSamples = 5)
    {
        // Recovery is destructive (it may clear _active.json), so it must own
        // the same cross-process lease as a running session first.
        var ownership = TryAcquireActiveSessionOwnership();
        if (ownership is null)
            return null;
        using (ownership)
        {
            var tombstone = ReadActiveTombstone();
            if (tombstone == ActiveTombstoneKind.Unknown)
            {
                // Unknown/garbled marker content is not authorization to drop
                // recoverable data. Isolate only the marker for inspection.
                QuarantineUnknownActiveTombstoneOwned();
                return null;
            }

            // A cancel tombstone wins over recovery. If deletion previously
            // failed, retry only the cleanup and never promote the data.
            if (tombstone is ActiveTombstoneKind.Cancelled or ActiveTombstoneKind.Handled)
            {
                // Preserve a newer build's active snapshot regardless of the
                // marker beside it; only that build can interpret its schema.
                if (IsFutureSchemaActiveSnapshot())
                    return null;
                var cancelledSnapshot = LoadActiveSnapshot();
                if (!HasActiveSnapshot)
                    ClearActiveCancellationOwned();
                else if (cancelledSnapshot is not null && ClearActiveSnapshotOwned())
                    ClearActiveCancellationOwned();
                return null;
            }

            var snapshot = LoadActiveSnapshot();
            if (snapshot is null)
                return null;
            if (snapshot.Samples.Count < Math.Max(0, minSamples))
            {
                MarkActiveSnapshotCancelledOwned();
                if (ClearActiveSnapshotOwned())
                    ClearActiveCancellationOwned();
                return null;
            }

            var recoveryId = RecoveryIdFor(snapshot);
            var recoveryPath = Path.Combine(SessionsDir, FileNameFor(recoveryId));
            if (TryLoadFile(recoveryPath, out var existing, out var existingError) && existing is not null)
            {
                // The historical copy was already promoted. Only retry clearing the
                // source snapshot; a failed delete must not create another Id.
                if (ClearActiveSnapshotOwned())
                    ClearActiveCancellationOwned();
                return existing;
            }
            if (existingError?.Contains("过新", StringComparison.Ordinal) == true)
            {
                // Never overwrite a newer build's historical file on an Id collision.
                return null;
            }

            var recovered = snapshot with
            {
                Id = recoveryId,
                Name = string.IsNullOrWhiteSpace(snapshot.Name) ? "（上次中断的会话）" : snapshot.Name + "（中断恢复）",
                EndedAt = snapshot.Samples.Count > 0 ? snapshot.Samples[^1].T : snapshot.StartedAt
            };
            try
            {
                // Save first. If the disk is full or the directory is unavailable,
                // keeping _active.json preserves the only recoverable copy.
                Save(recovered);
            }
            catch
            {
                return null;
            }
            if (ClearActiveSnapshotOwned())
                ClearActiveCancellationOwned();
            return recovered;
        }
    }

    private static string RecoveryIdFor(PerformanceSession snapshot)
    {
        // Exclude the active marker itself so the same snapshot has the same
        // legal 32-hex recovery Id across retries and process restarts.
        var canonical = snapshot with { Id = "" };
        var json = JsonSerializer.Serialize(canonical, JsonOpts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    // ---------- 标识与文件名 ----------

    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    public static string FileNameFor(string id)
    {
        if (!IsValidSessionId(id))
            throw new ArgumentException("会话 id 必须是 32 位十六进制标识", nameof(id));
        return id + ".json";
    }

    public static bool IsValidSessionId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.All(c => Uri.IsHexDigit(c))
           && id.Length is 32;

    /// <summary>只认 <32位hex>.json 的会话文件；_active.json、.corrupt 等不参与保留策略。</summary>
    public static bool IsManagedSessionFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsValidSessionId(name[..^5]);
    }

    private static void EnsureValidSessionForSave(PerformanceSession session)
    {
        if (session is null)
            throw new ArgumentNullException(nameof(session));
        if (!TryValidateSession(session, allowActiveId: false, out var error))
            throw new ArgumentException(error, nameof(session));
    }

    /// <summary>
    /// 拒绝语法上是 JSON、但结构不完整或数值越界的文件，避免坏数据进入
    /// UI/洞察并在后续恢复时被当成可信会话。
    /// </summary>
    private static bool TryValidateSession(PerformanceSession? session, bool allowActiveId, out string error)
    {
        error = "";
        if (session is null)
        {
            error = "会话数据为空";
            return false;
        }
        if (string.IsNullOrWhiteSpace(session.Id) ||
            (!allowActiveId && !IsValidSessionId(session.Id)))
        {
            error = "会话 id 不完整或不合法";
            return false;
        }
        if (string.IsNullOrWhiteSpace(session.Name))
        {
            error = "会话名称不完整";
            return false;
        }
        if (session.StartedAt == default)
        {
            error = "会话开始时间不完整";
            return false;
        }
        if (session.EndedAt is { } ended && ended < session.StartedAt)
        {
            error = "会话结束时间早于开始时间";
            return false;
        }
        if (!double.IsFinite(session.IntervalSeconds) || session.IntervalSeconds <= 0)
        {
            error = "采样间隔不合法";
            return false;
        }
        if (session.Samples is null)
        {
            error = "样本列表缺失";
            return false;
        }
        if (session.Samples.Count > MaxSamplesPerSession)
        {
            error = "样本数量超过本地缓冲上限";
            return false;
        }
        if (session.VramTotalMib is { } total &&
            (!double.IsFinite(total) || total <= 0))
        {
            error = "显存容量不合法";
            return false;
        }
        foreach (var sample in session.Samples)
        {
            if (sample is null || sample.T == default)
            {
                error = "样本时间不完整";
                return false;
            }
            if (!IsPercent(sample.CpuPct) || !IsPercent(sample.MemPct) || !IsPercent(sample.GpuPct))
            {
                error = "百分比样本超出 0-100 或不是有限数值";
                return false;
            }
            if (sample.VramUsedMib is { } used &&
                (!double.IsFinite(used) || used < 0))
            {
                error = "显存用量样本不合法";
                return false;
            }
        }
        return true;
    }

    private static bool IsPercent(double? value)
        => value is null || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 100);

    private sealed class ActiveSessionLease : IDisposable
    {
        private FileStream? _stream;
        private readonly string _lockPath;

        public ActiveSessionLease(FileStream stream, string lockPath)
        {
            _stream = stream;
            _lockPath = lockPath;
        }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
                return;
            try
            {
                lock (ActiveOwnershipSync)
                {
                    try { stream.Dispose(); }
                    finally
                    {
                        if (ReferenceEquals(ActiveOwner, stream))
                            ActiveOwner = null;
                    }
                }
            }
            finally
            {
                // The handle must be closed before attempting cleanup. A
                // competing process may acquire the same lock in this window;
                // in that case deletion fails harmlessly and must not revoke
                // the other process's ownership.
                try { File.Delete(_lockPath); }
                catch { /* stale lock cleanup is best effort */ }
            }
        }
    }
}
