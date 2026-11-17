using System.IO;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 一次性把旧 <c>verdicts.json</c>（A/B 实验的判定存储）导入 <c>gain-archive.json</c>。
///
/// 为什么要有这一步：判定存储合并后只保留收益档案，旧文件里已有用户跑过的结论不能丢。
/// 导入是"只加不改"：内容键去重交给 <see cref="GainArchiveService.Append"/>，重复运行不会翻倍；
/// 导入成功把原文件改名成 <c>verdicts.json.imported</c> 留档，失败则原样保留、下次启动再试。
/// 解析用 <see cref="JsonDocument"/> 而不是旧模型——旧类型随 A/B 退休已删除，导入器只认字段名。
/// </summary>
public static class LegacyVerdictImport
{
    /// <summary>测试注入：旧文件路径（默认为用户数据根下的 verdicts.json）。</summary>
    internal static string? SourceOverride;

    private static string SourcePath => SourceOverride ?? Path.Combine(UserDataPaths.Root, "verdicts.json");

    /// <summary>导入旧判定；返回导入了多少条（0 = 没有旧文件或没有可导入条目）。</summary>
    public static int RunOnce()
    {
        var path = SourcePath;
        if (!File.Exists(path))
            return 0;

        var imported = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return 0;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var entry = Map(item);
                if (entry is null) continue;
                GainArchiveService.Append(entry);
                imported++;
            }

            // 留档而不是删除：用户若想核对导入了什么，原文件还在。
            File.Move(path, path + ".imported", overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("legacy-verdicts-import", ex);
            return imported;
        }
        return imported;
    }

    private static GainArchiveEntry? Map(JsonElement item)
    {
        try
        {
            var items = item.TryGetProperty("Items", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : [];
            if (items.Length == 0)
                return null;

            var gamePath = String(item, "GamePath");
            var gameName = String(item, "Game");
            var gameId = !string.IsNullOrWhiteSpace(gamePath)
                ? Path.GetFileNameWithoutExtension(gamePath)
                : string.IsNullOrWhiteSpace(gameName) ? "unknown" : gameName!;

            var at = item.TryGetProperty("At", out var atElement) && atElement.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.Now;

            var baseFps = Number(item, "AvgFpsBase");
            var testFps = Number(item, "AvgFpsTest");
            var p1Base = Number(item, "P1LowBase");
            var p1Test = Number(item, "P1LowTest");
            var p99Base = Number(item, "P99Base");
            var p99Test = Number(item, "P99Test");

            return new GainArchiveEntry(
                GameId: gameId,
                At: at,
                Items: items,
                Verdict: item.TryGetProperty("Keep", out var keep) && keep.ValueKind == JsonValueKind.True ? "keep" : "rollback",
                AvgBase: baseFps,
                AvgTest: testFps,
                AvgDiffPct: Number(item, "DeltaPct"),
                AvgDiffLowerPct: Number(item, "AvgDiffLowerPct"),
                AvgDiffUpperPct: Number(item, "AvgDiffUpperPct"),
                P1DiffPct: Diff(p1Base, p1Test),
                P99DiffPct: Diff(p99Base, p99Test),
                SegmentsBefore: (int)(Number(item, "RoundsBase") ?? 0),
                SegmentsAfter: (int)(Number(item, "RoundsTest") ?? 0),
                SampleSeconds: (int)(Number(item, "DurationSec") ?? 0),
                BackupFile: null,
                RolledBack: item.TryGetProperty("Reverted", out var reverted) && reverted.ValueKind == JsonValueKind.True,
                MachineFingerprint: String(item, "EnvironmentHash") ?? GainArchiveService.CurrentFingerprint());
        }
        catch
        {
            return null;   // 单条坏数据跳过，不影响其它条目
        }
    }

    private static string? String(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            && double.IsFinite(d) ? d : null;

    private static double? Diff(double? before, double? after)
        => before is > 0 && after is { } a ? Math.Round((a - before.Value) / before.Value * 100, 2) : null;
}
