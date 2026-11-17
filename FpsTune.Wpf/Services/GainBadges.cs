using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 从收益档案推出「这个优化项在这台机器上测过没有」的徽标文案。
/// 0.5.0 之前这条信息来自 <c>verdicts.json</c>（A/B 实验写、优化页读）；现在统一读 <c>gain-archive.json</c>，
/// 一个档案既是历史趋势的数据源，也是徽标的数据源——不再有两套判定存储。
/// </summary>
public static class GainBadges
{
    /// <summary>
    /// 取某优化项在当前游戏下的徽标；null = 没有可用的对照记录（界面不显示徽标）。
    /// 规则与旧 <c>VerdictStore.BadgeFor</c> 一致：同游戏优先、取最新一条；
    /// 判定为 keep 的显示带符号增益，其余显示"已测·未保留"。
    /// </summary>
    public static string? For(string itemId, IReadOnlyList<GainArchiveEntry> entries, string? gameId)
    {
        if (entries.Count == 0)
            return null;

        var hit = entries
            .Where(e => e.Items.Contains(itemId, StringComparer.Ordinal))
            .Where(e => string.IsNullOrEmpty(gameId) || string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.At)
            .FirstOrDefault();
        if (hit is null)
            return null;

        return hit is { Verdict: "keep", AvgDiffPct: { } delta }
            ? Str.T("Str.VerdictBadge", delta)
            : Str.T("Str.VerdictLegacyBadge");
    }
}
