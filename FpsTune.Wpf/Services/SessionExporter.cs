using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 性能会话本地导出（JSON / CSV）。文件内容不含任何路径或用户名，
/// 字段、单位与缺失值语义在文件头部明确声明。
/// </summary>
public static class SessionExporter
{
    public static void ExportJson(PerformanceSession session, string path)
        => AtomicFile.WriteAllText(path, BuildJson(session), new UTF8Encoding(false));

    public static void ExportCsv(PerformanceSession session, string path)
        => AtomicFile.WriteAllText(path, BuildCsv(session), new UTF8Encoding(true)); // BOM：便于 Excel 直接打开

    public static string BuildJson(PerformanceSession session)
    {
        // 会话名称是用户输入，可能主动包含机器路径或用户名；导出时也按
        // 诊断包同一规则脱敏，避免“本地导出不含隐私”的承诺被名称绕过。
        var exportSession = session with { Name = PrivacyScrub.Sanitize(session.Name), Conditions = SessionConditionRules.Scrub(session.Conditions) };
        var payload = new
        {
            export = "fpstune-performance-session",
            schemaVersion = session.SchemaVersion,
            units = new
            {
                cpuPct = "百分比 0-100，null = 不可用",
                memPct = "百分比 0-100，null = 不可用",
                gpuPct = "百分比 0-100，null = 不可用",
                vramUsedMib = "MiB，null = 不可用",
                vramTotalMib = "MiB，null = 未能可靠取得"
            },
            session = exportSession
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildCsv(PerformanceSession session)
    {
        var sb = new StringBuilder();
        var conditions = SessionConditionRules.Scrub(session.Conditions);
        var metadata = conditions is null ? "" : string.Join(",", "", Cell(conditions.GameName), conditions.Width?.ToString(CultureInfo.InvariantCulture),
            conditions.Height?.ToString(CultureInfo.InvariantCulture), Cell(conditions.Quality), Cell(conditions.Scene),
            conditions.TargetSeconds?.ToString(CultureInfo.InvariantCulture), Fmt(session.IntervalSeconds),
            Fmt(session.EndedAt is { } end ? (end - session.StartedAt).TotalSeconds : null));
        sb.AppendLine("schema_version,timestamp_iso,cpu_pct,mem_pct,gpu_pct,vram_used_mib" +
            (conditions is null ? "" : ",game,resolution_width,resolution_height,quality,scene,target_seconds,interval_seconds,duration_seconds"));
        foreach (var s in session.Samples)
        {
            sb.Append(session.SchemaVersion).Append(',')
              .Append(s.T.ToString("O", CultureInfo.InvariantCulture))
              .Append(',').Append(Fmt(s.CpuPct))
              .Append(',').Append(Fmt(s.MemPct))
              .Append(',').Append(Fmt(s.GpuPct))
              .Append(',').Append(Fmt(s.VramUsedMib)).Append(metadata)
              .Append('\n');
        }
        return sb.ToString();
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if ("=+-@\t\r\n".Contains(value.TrimStart().FirstOrDefault())) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>缺失值输出空单元格；数值用不变文化（小数点为 .）。</summary>
    private static string Fmt(double? v)
        => v is double d && double.IsFinite(d) ? d.ToString("0.##", CultureInfo.InvariantCulture) : "";
}
