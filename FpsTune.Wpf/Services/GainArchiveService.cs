using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

/// <summary>导出/导入预演结果。导入不覆盖更可信的既有数据：重复条目跳过，不静默改写。</summary>
public sealed record GainArchiveImportPreview(
    bool Ok, string? Error, int SchemaVersion, int IncomingCount, int NewCount, int DuplicateCount);

/// <summary>
/// 每游戏收益档案（0.5.0 M3）：本机对照结论的长期记录，写入与读取全部本地。
/// 文件是 UserDataPaths.Root\gain-archive.json；损坏留档 .corrupt、未来 schema 留档
/// .future-<时间戳> 后按空档案继续（不崩、不静默改写）。容量有界（截断最旧）。
/// </summary>
public static class GainArchiveService
{
    /// <summary>档案容量上限：超过按最旧截断（对照是低频操作，200 条覆盖约数年使用）。</summary>
    public const int MaxEntries = 200;

    /// <summary>测试注入：容量上限（默认 MaxEntries）。</summary>
    internal static int CapacityOverride;

    private static int Capacity => CapacityOverride > 0 ? CapacityOverride : MaxEntries;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>测试注入：档案文件路径（默认用户数据根）。</summary>
    internal static string? FilePathOverride;

    /// <summary>测试注入：机器指纹（默认 GPU 名+驱动版本+CPU 名+逻辑核数）。</summary>
    internal static Func<string>? FingerprintOverride;

    internal static string FilePath => FilePathOverride
        ?? Path.Combine(UserDataPaths.Root, "gain-archive.json");

    /// <summary>机器指纹：指纹变化（换卡/换机/驱动大版本）时旧结论只作参考，UI 会提示。</summary>
    public static string CurrentFingerprint()
    {
        if (FingerprintOverride is { } injected) return injected();
        var hardware = HardwareInfoService.Get();
        return GainArchiveEntry.Fingerprint(hardware.Gpu, hardware.Cpu, DriverVersion());
    }

    private static string DriverVersion()
    {
        try
        {
            var key = NativeSystem.GetMainGpuDriverKeyPath();
            return key is null ? "unavailable" : RegistryHelper.ReadValue(
                RegistryHive.LocalMachine, key, "DriverVersion")?.ToString() ?? "unavailable";
        }
        catch { return "unavailable"; }
    }

    /// <summary>读取档案。损坏 / 未来 schema 时留档原文件并返回空档案；读取失败同样按空处理。</summary>
    public static GainArchive Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new GainArchive(GainArchive.CurrentSchemaVersion, []);
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var archive = JsonSerializer.Deserialize<GainArchive>(json, JsonOpts);
            if (archive is null)
            {
                AtomicFile.PreserveCorrupt(FilePath);
                return new GainArchive(GainArchive.CurrentSchemaVersion, []);
            }
            if (archive.SchemaVersion > GainArchive.CurrentSchemaVersion)
            {
                // 未来版本：原文件留档拒绝改写；本版本按空档案继续工作。
                PreserveFuture(FilePath);
                return new GainArchive(GainArchive.CurrentSchemaVersion, []);
            }
            return archive with { Entries = archive.Entries.TakeLast(Capacity).ToList() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Text.Json.JsonException or InvalidOperationException)
        {
            try { AtomicFile.PreserveCorrupt(FilePath); } catch { /* 留档失败不阻塞读取。 */ }
            ErrorLog.Write("gain-archive", ex);
            return new GainArchive(GainArchive.CurrentSchemaVersion, []);
        }
    }

    private static void PreserveFuture(string path)
    {
        try { File.Copy(path, path + ".future-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), overwrite: true); }
        catch { /* 留档失败不阻塞。 */ }
    }

    /// <summary>追加一条对照记录：同键去重、容量截断、原子落盘。失败静默（档案是辅助记录，不阻塞对照）。</summary>
    public static void Append(GainArchiveEntry entry)
    {
        try
        {
            var archive = Load();
            if (archive.Entries.Any(e => e.DedupeKey == entry.DedupeKey)) return;
            var entries = archive.Entries.Append(entry).TakeLast(Capacity).ToList();
            AtomicFile.WriteAllTextDurable(FilePath,
                JsonSerializer.Serialize(new GainArchive(GainArchive.CurrentSchemaVersion, entries), JsonOpts),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ErrorLog.Write("gain-archive", ex);
        }
    }

    /// <summary>某游戏的最佳一次确认收益（值得保留里 diffPct 最高的）与最近对照时间。</summary>
    public static (GainArchiveEntry? Best, GainArchiveEntry? Latest, int Count) Summary(
        GainArchive archive, string gameId)
    {
        var entries = archive.Entries
            .Where(e => string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.At).ToList();
        var best = entries
            .Where(e => string.Equals(e.Verdict, "keep", StringComparison.OrdinalIgnoreCase) && e.AvgDiffPct is { })
            .OrderByDescending(e => e.AvgDiffPct!.Value).FirstOrDefault();
        return (best, entries.Count > 0 ? entries[^1] : null, entries.Count);
    }

    /// <summary>导出当前档案（单 JSON，带 schemaVersion 与导出时间）。</summary>
    public static int Export(string path)
    {
        var archive = Load();
        var payload = new { exportedAt = DateTimeOffset.Now, archive.SchemaVersion, archive.Entries };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOpts), new UTF8Encoding(false));
        return archive.Entries.Count;
    }

    /// <summary>导入预演：只读外部文件并统计会新增几条、跳过几条重复，不写任何数据。</summary>
    public static GainArchiveImportPreview PreviewImport(string path)
    {
        return ReadExternal(path) is not { } incoming
            ? new GainArchiveImportPreview(false, "文件无法解析，或不是本程序导出的收益档案。", 0, 0, 0, 0)
            : CountPreview(incoming);
    }

    /// <summary>执行导入：跳过与现有重复的条目（同键即同一条），新增条目并入并落盘。</summary>
    public static GainArchiveImportPreview Import(string path)
    {
        if (ReadExternal(path) is not { } incoming)
            return new GainArchiveImportPreview(false, "文件无法解析，或不是本程序导出的收益档案。", 0, 0, 0, 0);
        var preview = CountPreview(incoming);
        if (!preview.Ok) return preview;
        if (preview.NewCount == 0)
            return preview;

        try
        {
            var archive = Load();
            var existing = archive.Entries.Select(e => e.DedupeKey).ToHashSet(StringComparer.Ordinal);
            var added = incoming.Entries
                .Where(e => existing.Add(e.DedupeKey))
                .OrderBy(e => e.At).ToList();
            if (added.Count == 0) return preview;
            var entries = archive.Entries.Concat(added).TakeLast(Capacity).ToList();
            AtomicFile.WriteAllTextDurable(FilePath,
                JsonSerializer.Serialize(new GainArchive(GainArchive.CurrentSchemaVersion, entries), JsonOpts),
                new UTF8Encoding(false));
            return preview;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ErrorLog.Write("gain-archive", ex);
            return new GainArchiveImportPreview(false, "写入失败：" + ex.Message, preview.SchemaVersion, preview.IncomingCount, 0, preview.DuplicateCount);
        }
    }

    private static GainArchive? ReadExternal(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, Encoding.UTF8);
            // 导出文件两种形态都收：裸档案或带导出时间的包裹。
            if (JsonSerializer.Deserialize<GainArchive>(json, JsonOpts) is { } bare && bare.Entries.Count > 0)
                return bare.SchemaVersion > GainArchive.CurrentSchemaVersion ? null : bare;
            if (JsonSerializer.Deserialize<ExportPayload>(json, JsonOpts) is { } wrapped)
            {
                if (wrapped.SchemaVersion > GainArchive.CurrentSchemaVersion) return null;
                return new GainArchive(wrapped.SchemaVersion, wrapped.Entries ?? []);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Text.Json.JsonException or InvalidOperationException)
        {
            ErrorLog.Write("gain-archive", ex);
            return null;
        }
    }

    private sealed record ExportPayload(DateTimeOffset ExportedAt, int SchemaVersion, IReadOnlyList<GainArchiveEntry>? Entries);

    private static GainArchiveImportPreview CountPreview(GainArchive incoming)
    {
        var existing = Load().Entries.Select(e => e.DedupeKey).ToHashSet(StringComparer.Ordinal);
        var duplicates = incoming.Entries.Count(e => existing.Contains(e.DedupeKey));
        return new GainArchiveImportPreview(true, null, incoming.SchemaVersion,
            incoming.Entries.Count, incoming.Entries.Count - duplicates, duplicates);
    }
}
