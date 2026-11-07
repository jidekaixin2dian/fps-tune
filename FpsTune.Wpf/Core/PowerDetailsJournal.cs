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
    DateTimeOffset At);

/// <summary>
/// <c>%LOCALAPPDATA%\FpsTune\power-details.json</c> 的读写。
/// - 原子写入（<see cref="AtomicFile"/>）；
/// - 损坏文件按 <see cref="AtomicFile.PreserveCorrupt"/> 留档后当空表，不让坏文件卡住面板；
/// - 同一项的第一次快照不会被后来的改动覆盖（否则「恢复」只能退回上一次改动）。
/// </summary>
internal static class PowerDetailsJournal
{
    private const int CurrentVersion = 1;

    internal static string? PathOverride { get; set; }

    internal static string JournalPath => PathOverride ?? Path.Combine(UserDataPaths.Root, "power-details.json");

    private sealed record Document(int Version, List<PowerDetailsJournalEntry> Entries);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal static IReadOnlyList<PowerDetailsJournalEntry> Load()
    {
        var path = JournalPath;
        if (!File.Exists(path)) return [];
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
            if (document?.Entries is null) return [];
            return document.Entries.Where(IsUsable).ToList();
        }
        catch (Exception)
        {
            AtomicFile.PreserveCorrupt(path);
            return [];
        }
    }


    /// <summary>
    /// 记录改动前的取值。<paramref name="ac"/> / <paramref name="dc"/> 至少一个可读才有恢复价值；
    /// 已有同项快照时保持原快照（第一次改动前的值才是「恢复」目标）。
    /// </summary>
    internal static void Record(Guid scheme, PowerSettingEntry entry, uint? ac, uint? dc)
    {
        if (ac is null && dc is null) return;

        var entries = Load().ToList();
        var exists = entries.Any(e => Matches(e, scheme, entry.Subgroup, entry.Setting));
        if (exists) return;

        entries.Add(new PowerDetailsJournalEntry(
            scheme.ToString("D"),
            entry.Subgroup.ToString("D"),
            entry.Setting.ToString("D"),
            ac,
            dc,
            entry.Name,
            DateTimeOffset.Now));
        Save(entries);
    }

    internal static void Remove(Guid scheme, Guid subgroup, Guid setting)
    {
        var entries = Load().ToList();
        if (entries.RemoveAll(e => Matches(e, scheme, subgroup, setting)) == 0) return;
        Save(entries);
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
        AtomicFile.WriteAllText(JournalPath, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
    }

    private static bool Matches(PowerDetailsJournalEntry entry, Guid scheme, Guid subgroup, Guid setting)
        => string.Equals(entry.Scheme, scheme.ToString("D"), StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.Subgroup, subgroup.ToString("D"), StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.Setting, setting.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(PowerDetailsJournalEntry entry)
        => !string.IsNullOrWhiteSpace(entry.Scheme)
           && !string.IsNullOrWhiteSpace(entry.Subgroup)
           && !string.IsNullOrWhiteSpace(entry.Setting)
           && Guid.TryParse(entry.Scheme, out _)
           && Guid.TryParse(entry.Subgroup, out _)
           && Guid.TryParse(entry.Setting, out _)
           && (entry.Ac.HasValue || entry.Dc.HasValue);

}
