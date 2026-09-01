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
        => File.WriteAllText(path, BuildJson(session), new UTF8Encoding(false));

    public static void ExportCsv(PerformanceSession session, string path)
        => File.WriteAllText(path, BuildCsv(session), new UTF8Encoding(true)); // BOM：便于 Excel 直接打开

    public static string BuildJson(PerformanceSession session)
    {
        var payload = new
        {
            export = "fpstune-performance-session",
            schemaVersion = PerformanceSessionStore.CurrentSchemaVersion,
            units = new
            {
                cpuPct = "百分比 0-100，null = 不可用",
                memPct = "百分比 0-100，null = 不可用",
                gpuPct = "百分比 0-100，null = 不可用",
                vramUsedMib = "MiB，null = 不可用"
            },
            session = session
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildCsv(PerformanceSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("schema_version,timestamp_iso,cpu_pct,mem_pct,gpu_pct,vram_used_mib");
        foreach (var s in session.Samples)
        {
            sb.Append(PerformanceSessionStore.CurrentSchemaVersion).Append(',')
              .Append(s.T.ToString("O"))
              .Append(',').Append(Fmt(s.CpuPct))
              .Append(',').Append(Fmt(s.MemPct))
              .Append(',').Append(Fmt(s.GpuPct))
              .Append(',').Append(Fmt(s.VramUsedMib))
              .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>缺失值输出空单元格；数值用不变文化（小数点为 .）。</summary>
    private static string Fmt(double? v)
        => v is double d && double.IsFinite(d) ? d.ToString("0.##", CultureInfo.InvariantCulture) : "";
}
