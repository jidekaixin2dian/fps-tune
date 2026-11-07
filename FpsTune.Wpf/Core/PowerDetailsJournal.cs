using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 一条「改动前」快照：方案 / 分组 / 设置 / 两套原值 / 时间 / 名称。
/// 恢复只按这份快照写回，所以「恢复本次改动」回到的是**第一次改之前的值**，
/// 不是系统默认值（系统默认值由「恢复默认」那条路径单独负责）。
/// </summary>
internal sealed record PowerDetailsJournalEntry(
    string Scheme,
    string Subgroup,
    string Setting,
    uint? Ac,
    uint? Dc,
    string Name,
    DateTimeOffset At,
    uint? AcTarget = null,
    uint? DcTarget = null,
    uint? AcBeforeAttempt = null,
    uint? DcBeforeAttempt = null)
{
    internal bool HasLegacyValues => (Ac.HasValue && !AcTarget.HasValue) || (Dc.HasValue && !DcTarget.HasValue);
}

/// <summary>
/// <c>%LOCALAPPDATA%\FpsTune\power-details.json</c> 的读写。
/// - 原子写入（<see cref="AtomicFile"/>）；
/// - 损坏文件按 <see cref="AtomicFile.PreserveCorrupt"/> 留档后当空表，不让坏文件卡住面板；
/// - 同一项的第一次快照不会被后来的改动覆盖（否则「恢复」只能退回上一次改动）。
/// </summary>
internal static class PowerDetailsJournal
{
    private const int CurrentVersion = 2;
    internal const string FileName = "power-details.json";

    internal static string? PathOverride { get; set; }

    internal static string JournalPath => PathOverride ?? Path.Combine(UserDataPaths.Root, FileName);

    private sealed record Document(int Version, List<PowerDetailsJournalEntry> Entries);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal static void ValidateTransferredBackup(byte[] bytes)
    {
        var document = Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if (document.Entries.Any(e => !IsUsable(e))
            || document.Entries.Select(e => (Guid.Parse(e.Scheme), Guid.Parse(e.Subgroup), Guid.Parse(e.Setting))).Distinct().Count() != document.Entries.Count)
            throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));
    }

    internal static bool IsEmptyBackup(byte[] bytes)
    {
        try { return Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')).Entries.Count == 0; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return false; }
    }

    private static Document Parse(string json)
        => JsonSerializer.Deserialize<Document>(json, JsonOptions) is { Version: 1 or CurrentVersion, Entries: not null } document
            ? document
            : throw new InvalidDataException(Str.T("Str.BackupFormatInvalid"));

    internal static IReadOnlyList<PowerDetailsJournalEntry> Load()
    {
        var path = JournalPath;
        try
        {
            return Parse(File.ReadAllText(path)).Entries.Where(IsUsable).ToList();
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException)
        {
            AtomicFile.PreserveCorrupt(path);
            return [];
        }
    }


    /// <summary>
    /// 记录改动前的取值。<paramref name="ac"/> / <paramref name="dc"/> 至少一个可读才有恢复价值；
    /// 已有同项快照时保持原快照（第一次改动前的值才是「恢复」目标）。
    /// </summary>
    internal static PowerDetailsJournalEntry? Record(
        Guid scheme, PowerSettingEntry entry, uint? ac, uint? dc, uint? acTarget, uint? dcTarget)
    {
        if (ac is null && dc is null) return null;

        var entries = Load().ToList();
        var index = entries.FindIndex(e => Matches(e, scheme, entry.Subgroup, entry.Setting));
        var original = index >= 0 ? entries[index] : new PowerDetailsJournalEntry(
            scheme.ToString("D"), entry.Subgroup.ToString("D"), entry.Setting.ToString("D"),
            null, null, entry.Name, DateTimeOffset.Now);
        var pending = original with
        {
            Ac = original.Ac ?? ac,
            Dc = original.Dc ?? dc,
            AcTarget = ac.HasValue ? acTarget : original.AcTarget,
            DcTarget = dc.HasValue ? dcTarget : original.DcTarget,
            AcBeforeAttempt = ac ?? original.AcBeforeAttempt,
            DcBeforeAttempt = dc ?? original.DcBeforeAttempt
        };
        if (index >= 0) entries[index] = pending;
        else entries.Add(pending);
        Save(entries);
        return pending;
    }

    internal static void Confirm(PowerDetailsJournalEntry pending, bool ac, bool dc)
    {
        var confirmed = pending with
        {
            AcBeforeAttempt = ac ? null : pending.AcBeforeAttempt,
            DcBeforeAttempt = dc ? null : pending.DcBeforeAttempt
        };
        if (confirmed == pending) return;
        Save(Load().Select(entry => entry == pending ? confirmed : entry).ToList());
    }

    internal static void Consume(IReadOnlyList<PowerDetailsJournalEntry> entries, HashSet<PowerDetailsJournalEntry> restored)
    {
        var remaining = entries.Where(entry => !restored.Contains(entry)).ToList();
        if (remaining.Count == 0) File.Delete(JournalPath);
        else Save(remaining);
    }

    internal static void Clear()
    {
        try
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
        }
        catch (IOException)
        {
            // 删不掉就留空表，不影响面板。
            Save([]);
        }
    }

    private static void Save(IReadOnlyList<PowerDetailsJournalEntry> entries)
    {
        var document = new Document(CurrentVersion, entries.ToList());
        AtomicFile.WriteAllTextDurable(JournalPath, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
    }

    private static bool Matches(PowerDetailsJournalEntry entry, Guid scheme, Guid subgroup, Guid setting)
        => string.Equals(entry.Scheme, scheme.ToString("D"), StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.Subgroup, subgroup.ToString("D"), StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.Setting, setting.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(PowerDetailsJournalEntry? entry)
        => entry is not null
           && !string.IsNullOrWhiteSpace(entry.Scheme)
           && !string.IsNullOrWhiteSpace(entry.Subgroup)
           && !string.IsNullOrWhiteSpace(entry.Setting)
           && Guid.TryParse(entry.Scheme, out _)
           && Guid.TryParse(entry.Subgroup, out _)
           && Guid.TryParse(entry.Setting, out _)
           && (entry.Ac.HasValue || entry.Dc.HasValue);

}
