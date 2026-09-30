using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 0.2.0 M1：实测判定沉淀存储。真实模式（非 Simulate）的每次实验结论在此 upsert，
/// 供优化项徽标 / A/B 页结论列表 / 可引用报告导出读取。
/// 红线「数据说话」：Simulate 模拟数据绝不入册；DeltaPct 为实测差值，不外推、不编造。
/// </summary>
public static class VerdictStore
{
    private const string FileName = "verdicts.json";
    internal static string? DirOverride { get; set; }

    private static string Dir => DirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune");
    private static string FilePath => Path.Combine(Dir, FileName);

    /// <summary>一条实测结论。Items 单元素 = 项级（单项深测），多元素 = 整套实测。</summary>
    public sealed record VerdictEntry(
        string Key,
        string Kind,
        IReadOnlyList<string> Items,
        string GroupId,
        string? Game,
        double AvgFpsBase,
        double AvgFpsTest,
        double P1LowBase,
        double P1LowTest,
        int StuttersBase,
        int StuttersTest,
        bool Stable,
        bool Keep,
        double DeltaPct,
        DateTime At,
        int DurationSec,
        string Mode);

    /// <summary>规范化键：按 id 排序后 "|" 连接，保证同集合幂等。</summary>
    public static string MakeKey(IEnumerable<string> itemIds)
        => string.Join("|", itemIds.OrderBy(id => id, StringComparer.Ordinal));

    /// <summary>同 Key 覆盖写（最新实测取代旧结论）。写入失败静默——判定存储不阻塞实验流程。</summary>
    public static void Upsert(VerdictEntry entry)
    {
        try
        {
            var entries = Load();
            entries.RemoveAll(v => v.Key == entry.Key);
            entries.Add(entry);
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Directory.CreateDirectory(Dir);
            AtomicFile.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(entries, JsonOpts),
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 判定存储失败不影响实验本身；下次实验会再次尝试写入。
        }
    }

    public static List<VerdictEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            return JsonSerializer.Deserialize<List<VerdictEntry>>(File.ReadAllText(FilePath), JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>取单优化项的最新判定（项级徽标数据源）；无记录返回 null。</summary>
    public static VerdictEntry? FindItem(string itemId)
        => Load().FirstOrDefault(v => v.Kind == "item" && v.Items.Count == 1 && v.Items[0] == itemId);

    /// <summary>项级徽标文案（本机实测 Δ 平均帧率百分比，带符号；无记录返回 null）。
    /// 0.2.0 M2：按当前游戏过滤——同一优化项在不同游戏的实测结论互不串台。</summary>
    public static string? BadgeFor(string itemId, IReadOnlyList<VerdictEntry>? preloaded = null, string? gameName = null)
    {
        bool Match(VerdictEntry v) => v.Kind == "item" && v.Items.Count == 1 && v.Items[0] == itemId
            && (gameName is null || string.Equals(v.Game, gameName, StringComparison.OrdinalIgnoreCase));
        var hit = preloaded is null
            ? Load().FirstOrDefault(Match)
            : preloaded.FirstOrDefault(Match);
        return hit is null ? null : Str.T("Str.VerdictBadge", hit.DeltaPct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
