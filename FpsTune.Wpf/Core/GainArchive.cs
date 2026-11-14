using System.Globalization;

namespace FpsTune.Wpf.Core;

/// <summary>一次前后对照的收益记录（0.5.0 M3 收益档案的最小条目）。数值口径与对照结论卡一致。</summary>
public sealed record GainArchiveEntry(
    string GameId,
    DateTimeOffset At,
    IReadOnlyList<string> Items,
    string Verdict,
    double? AvgBase,
    double? AvgTest,
    double? AvgDiffPct,
    double? AvgDiffLowerPct,
    double? AvgDiffUpperPct,
    double? P1DiffPct,
    double? P99DiffPct,
    int SegmentsBefore,
    int SegmentsAfter,
    int SampleSeconds,
    string? BackupFile,
    bool RolledBack,
    string MachineFingerprint)
{
    /// <summary>
    /// 去重键 = 内容键（不含时间）：同一游戏、同一批项目、同一结论与数值的对照视为同一条，
    /// 重复追加（含导出导入往返）只留一份；不同内容的对照即使同一分钟也是两条独立记录。
    /// </summary>
    internal string DedupeKey => string.Create(CultureInfo.InvariantCulture,
        $"{GameId}|{string.Join(",", Items)}|{Verdict}|{AvgBase:0.##}|{AvgTest:0.##}|{AvgDiffPct:0.##}|{AvgDiffLowerPct:0.##}|{AvgDiffUpperPct:0.##}|{P1DiffPct:0.##}|{P99DiffPct:0.##}|{SegmentsBefore}|{SegmentsAfter}|{SampleSeconds}|{RolledBack}|{MachineFingerprint}");

    internal static string Fingerprint(string gpu, string cpu, string driverVersion) =>
        string.Create(CultureInfo.InvariantCulture, $"{gpu}|{driverVersion}|{cpu}|{Environment.ProcessorCount}");
}

/// <summary>收益档案文件模型。未来版本整体拒绝并留档，不静默改写。</summary>
public sealed record GainArchive(int SchemaVersion, IReadOnlyList<GainArchiveEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}
