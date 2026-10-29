using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace FpsTune.Wpf.Services;

internal static class SessionComparisonReport
{
    internal static void Export(SessionComparison comparison, string path)
        => AtomicFile.WriteAllText(path, BuildHtml(comparison), new UTF8Encoding(false));

    internal static string BuildHtml(SessionComparison comparison)
    {
        // Only encoded, scrubbed display values are inserted. No paths, scripts, or remote assets.
        static string H(string? text) => WebUtility.HtmlEncode(PrivacyScrub.Sanitize(text));
        static string Width(double value, double maximum) => Math.Clamp(value / maximum * 100, 0, 100).ToString("0.##", CultureInfo.InvariantCulture);
        var a = comparison.A; var b = comparison.B;
        var html = new StringBuilder("""
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none';style-src 'unsafe-inline';img-src 'none'">
<title>FPS 帧律 · <!--REPORT_TITLE--></title><style>
*{box-sizing:border-box}body{margin:0;background:#f3f6f8;color:#192633;font:15px/1.6 'Segoe UI','Microsoft YaHei UI',sans-serif}
main{max-width:1060px;margin:36px auto;padding:36px;background:white;border:1px solid #dce4eb;border-radius:20px}
.brand{font-size:13px;letter-spacing:2px;color:#587185}h1{font-size:30px;line-height:1.25;margin:12px 0 24px}h2{font-size:19px;margin:28px 0 12px}
.sources,.highlights{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}.highlights{grid-template-columns:repeat(3,minmax(0,1fr))}
.source,.metric{border:1px solid #dce4eb;border-radius:14px;padding:18px;overflow-wrap:anywhere}.source strong{display:block;font-size:17px}
.muted,small{color:#607386}.status{padding:16px 20px;background:#edf4fb;border-radius:12px;margin:22px 0}.status.warning{background:#fff4e2;color:#77521b}
.status p{margin:0}.status ul{margin:8px 0 0;padding-left:22px}.metric h3{font-size:14px;font-weight:500;margin:0 0 12px}.change{font-size:25px;font-weight:600}
.good{color:#3a652b}.attention{color:#9a5d17}.neutral{color:#2c465e}.bar{height:5px;background:#edf1f5;margin:9px 0 13px;border-radius:4px;overflow:hidden}.bar i{display:block;height:100%;background:#63829c}.bar.b i{background:#507b3a}
.pair{font-size:13px;display:flex;justify-content:space-between;gap:8px}.relative{display:block;font-size:13px;margin:4px 0 15px}
table{border-collapse:collapse;width:100%;table-layout:fixed;font-size:13px}th,td{padding:11px 12px;border-bottom:1px solid #dce4eb;text-align:left;vertical-align:top;overflow-wrap:anywhere}th{background:#f5f8fa;color:#4c6478}th:first-child{width:24%}footer{margin-top:28px;font-size:12px;color:#607386}
@media(max-width:650px){main{margin:0;padding:22px;border:0;border-radius:0}.sources,.highlights{grid-template-columns:1fr}h1{font-size:25px}th,td{padding:8px 5px}}
@media print{@page{size:A4;margin:14mm}body{background:white;font-size:12px}main{margin:0;padding:0;border:0;max-width:none}h1{font-size:25px}.metric,.source,.status,tr{break-inside:avoid}thead{display:table-header-group}th,td{padding:7px}*{print-color-adjust:exact}}
</style></head><body><main><div class="brand">FPS 帧律 / PERFORMANCE</div><h1><!--REPORT_TITLE--></h1><div class="sources">
""");
        foreach (var item in new[] { (Label: Str.T("Str.ComparisonBaseline"), Session: a), (Label: Str.T("Str.ComparisonCandidate"), Session: b) })
            html.Append("<section class='source'><small>").Append(H(item.Label)).Append("</small><strong>")
                .Append(H(item.Session.Name)).Append("</strong><span class='muted'>").Append(item.Session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("</span></section>");
        html.Append("</div><section class='status ").Append(comparison.Issues.Count == 0 ? "" : "warning").Append("'><p>")
            .Append(H(Str.T(comparison.Issues.Count == 0 ? "Str.ConditionMatch" : "Str.ComparisonCheckConditions"))).Append("</p>");
        if (comparison.Issues.Count > 0)
        {
            html.Append("<ul>"); foreach (var issue in comparison.Issues) html.Append("<li>").Append(H(issue)).Append("</li>"); html.Append("</ul>");
        }
        html.Append("</section><h2>").Append(H(Str.T("Str.ComparisonHighlights"))).Append("</h2><div class='highlights'>");
        foreach (var h in comparison.Highlights)
        {
            html.Append("<section class='metric'><h3>").Append(H(h.Metric)).Append("</h3><div class='change ").Append(h.Trend).Append("'>")
                .Append(H(h.Delta)).Append("</div><span class='relative muted'>").Append(H(Str.T("Str.ComparisonRelative", h.Relative))).Append("</span>")
                .Append("<div class='pair'><span>A</span><span>").Append(H(h.AVal)).Append("</span></div><div class='bar'><i style='width:")
                .Append(Width(h.AChartValue, h.Maximum)).Append("%'></i></div><div class='pair'><span>B</span><span>").Append(H(h.BVal))
                .Append("</span></div><div class='bar b'><i style='width:").Append(Width(h.BChartValue, h.Maximum)).Append("%'></i></div></section>");
        }
        html.Append("</div><h2>").Append(H(Str.T("Str.ComparisonDetails"))).Append("</h2><table><thead><tr>");
        foreach (var key in new[] { "Str.CompareHeaderMetric", "Str.ComparisonBaseline", "Str.ComparisonCandidate", "Str.CompareHeaderDelta" })
            html.Append("<th>").Append(H(Str.T(key))).Append("</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var row in comparison.Rows)
        {
            html.Append("<tr>"); foreach (var value in new[] { row.Metric, row.AVal, row.BVal, row.Delta }) html.Append("<td>").Append(H(value)).Append("</td>"); html.Append("</tr>");
        }
        return html.Append("</tbody></table><footer>").Append(H(Str.T("Str.ComparisonReportFooter"))).Append("</footer></main></body></html>").ToString()
            .Replace("<!--REPORT_TITLE-->", H(Str.T("Str.ComparisonReportTitle")), StringComparison.Ordinal);
    }
}
