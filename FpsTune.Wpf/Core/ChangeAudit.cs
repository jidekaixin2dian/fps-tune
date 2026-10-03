using System.IO;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>改动总览一行：某优化项最近一次由本工具写下的改动，原值摘要 vs 当前实时状态。</summary>
public sealed record ChangeAuditRow(
    string ItemId,
    string ItemName,
    string Kind,
    DateTime BackedUpAt,
    string BackupSummary,
    bool OriginalExisted,
    bool OptimizedNow,
    string CurrentNow);

/// <summary>
/// 0.2.2 M4：改动总览数据收集——「本工具改过什么」。
/// 只读：不还原、不改任何设置（还原一律走既有入口，见 PLAN-0.2.0 M4 约束）。
/// 同一项多次备份时取最新一条未还原记录（代表工具当前生效的写入）。
/// </summary>
public static class ChangeAudit
{
    public static IReadOnlyList<ChangeAuditRow> CollectPending()
    {
        var rows = new List<ChangeAuditRow>();
        foreach (var group in BackupService.EnumeratePendingRecords()
                     .GroupBy(r => r.Record.Id, StringComparer.Ordinal))
        {
            var best = group.OrderByDescending(g => g.LastWrite).First();
            var record = best.Record;
            var def = ItemCatalog.All.FirstOrDefault(x => x.Id == record.Id);
            if (def is null)
                continue;

            var (optimized, current) = DetectionService.GetItemState(def, AppState.GamePath);
            rows.Add(new ChangeAuditRow(
                record.Id,
                def.DisplayName,
                record.Kind,
                best.LastWrite,
                DescribeBackup(record),
                record.Existed,
                optimized,
                current));
        }
        return rows.OrderBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string DescribeBackup(BackupRecord r) => r.Kind switch
    {
        "registry" => $"{RegistryHiveLabel(r.Hive)}\\{r.Path}\\{r.Name}"
            + (r.Existed ? " " + Str.T("Str.AuditOriginalValue", r.OldValue) : Str.T("Str.AuditValueAbsent")),
        "power-plan" => Str.T("Str.AuditPowerPlan", Short(r.OldActiveGuid)),
        "power-tuning" => Str.T("Str.AuditPowerTuning", Format(r.OldUsbValue), Format(r.OldBoostValue)),
        "power-aspm" => Str.T("Str.AuditAspm", Format(r.OldAspmValue)),
        "service" => Str.T("Str.AuditService", r.ServiceName, r.OldStartMode ?? r.OldStartValue.ToString()),
        "hibernate" or "bcdedit" => Str.T("Str.AuditState", r.OldState),
        _ => r.Kind,
    };

    private static string RegistryHiveLabel(string hive)
        => hive switch
        {
            "LocalMachine" => "HKLM",
            "CurrentUser" => "HKCU",
            _ => hive,
        };

    private static string Short(string? guid) => string.IsNullOrWhiteSpace(guid) ? "?" : guid.Length <= 8 ? guid : guid[..8] + "...";

    private static string Format(int? v) => v.HasValue ? v.Value.ToString() : Str.T("Str.AuditUnread");
}
