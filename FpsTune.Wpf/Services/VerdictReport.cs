using System.Text;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 0.2.0 M1 / P2-2：可引用实测报告导出（Markdown，语言随当前界面语言）。
/// 只包含 verdicts.json 里的实测记录与采样条件，不含任何预测或外推（红线「数据说话」）。
/// </summary>
public static class VerdictReport
{
    public static string Build(IReadOnlyList<VerdictStore.VerdictEntry> verdicts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Str.T("Str.ReportTitle")} — FPS Tune v{UpdateService.CurrentVersion}");
        sb.AppendLine();
        sb.AppendLine(Str.T("Str.ReportGenerated", DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        sb.AppendLine();
        sb.AppendLine($"## {Str.T("Str.ReportConditions")}");
        sb.AppendLine();
        var games = verdicts.Select(v => v.Game).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
        var durations = verdicts.Select(v => v.DurationSec).Distinct().ToList();
        var modes = verdicts.Select(v => v.Mode).Distinct().ToList();
        sb.AppendLine($"- {Str.T("Str.ReportCondGame")}: {(games.Count > 0 ? string.Join(", ", games) : "-")}");
        sb.AppendLine($"- {Str.T("Str.ReportCondSample")}: {string.Join(", ", durations.Select(d => $"{d}s ×3"))} ({string.Join(", ", modes)})");
        sb.AppendLine($"- {Str.T("Str.ReportCondTool")}: FPS Tune v{UpdateService.CurrentVersion} (.NET 10, PresentMon)");
        sb.AppendLine();
        sb.AppendLine($"## {Str.T("Str.ReportConclusionSection")}");
        sb.AppendLine();
        sb.AppendLine($"| # | {Str.T("Str.ReportColSet")} | {Str.T("Str.ReportColDelta")} | {Str.T("Str.ReportColP1Low")} | {Str.T("Str.ReportColVerdict")} | {Str.T("Str.ReportColDate")} |");
        sb.AppendLine("|---|---|---|---|---|---|");
        for (var i = 0; i < verdicts.Count; i++)
        {
            var v = verdicts[i];
            var items = string.Join(", ", v.Items.Select(id =>
                ItemCatalog.All.FirstOrDefault(x => x.Id == id)?.DisplayName ?? id));
            sb.AppendLine(
                $"| {i + 1} | {items} | {v.DeltaPct:+0.0;-0.0;0}% | {v.P1LowBase:0.#} → {v.P1LowTest:0.#} | {Str.T(v.Keep ? "Str.ReportKeep" : "Str.ReportRevert")} | {v.At:yyyy-MM-dd HH:mm} |");
        }
        sb.AppendLine();
        sb.AppendLine(Str.T("Str.ReportDisclaimer"));
        sb.AppendLine();
        sb.AppendLine(Str.T("Str.ReportSourceNote"));
        return sb.ToString();
    }
}
