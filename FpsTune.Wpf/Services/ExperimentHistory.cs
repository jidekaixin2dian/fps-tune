using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

/// <summary>一次实验记录（基线采样或候选组测试），历史趋势图的数据点。</summary>
public sealed record ExperimentRun(
    DateTime Time,
    string Kind,
    string Id,
    string Name,
    double AvgFps,
    double P1Low,
    bool? Keep,
    string? Reason)
{
    public string ShortLabel => Kind == "baseline" ? "基线" : Id.Replace("group-", "G", StringComparison.Ordinal);
}

/// <summary>
/// A/B 实验历史加载：优先读 tuning-experiment.ps1 追加的 history.jsonl，
/// 没有历史文件时回退到 state.json（把当前基线与候选组结果当作一次趋势）。
/// </summary>
public static class ExperimentHistory
{
    private static string ExperimentDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FpsTune", "experiment");

    public static string HistoryFile => Path.Combine(ExperimentDir, "history.jsonl");
    public static string StateFile => Path.Combine(ExperimentDir, "state.json");

    public static List<ExperimentRun> Load()
    {
        var runs = ParseLines(ReadAllLinesSafe(HistoryFile));
        if (runs.Count > 0)
            return runs;

        // 回退：老版本没有 history.jsonl，用当前 state.json 拼一次趋势
        try
        {
            if (!File.Exists(StateFile))
                return [];
            var root = JsonNode.Parse(File.ReadAllText(StateFile, Encoding.UTF8));
            if (root is null)
                return [];
            var list = new List<ExperimentRun>();
            if (root["baseline"]?["summary"] is JsonObject bs
                && ParseRun(DateTime.MinValue, "baseline", "baseline", "基线", bs, null, null) is { } b)
                list.Add(b);
            if (root["groups"] is JsonArray groups)
            {
                foreach (var g in groups)
                {
                    if (g?["summary"] is JsonObject sum
                        && ParseRun(DateTime.MinValue, "test",
                            g["id"]?.GetValue<string>() ?? "",
                            g["name"]?.GetValue<string>() ?? "",
                            sum,
                            g["keep"] is JsonNode k ? k.GetValue<bool>() : null,
                            g["reason"]?.GetValue<string>()) is { } r)
                        list.Add(r);
                }
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    public static List<ExperimentRun> ParseLines(IEnumerable<string> lines)
    {
        var runs = new List<ExperimentRun>();
        foreach (var raw in lines)
        {
            var line = raw.TrimStart('\uFEFF').Trim();
            if (line.Length == 0)
                continue;
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o)
                    continue;
                var time = DateTime.TryParse(o["time"]?.GetValue<string>(), out var t) ? t : DateTime.MinValue;
                var kind = o["kind"]?.GetValue<string>() ?? "test";
                var id = o["id"]?.GetValue<string>() ?? "";
                var name = o["name"]?.GetValue<string>() ?? id;
                if (o["summary"] is JsonObject sum
                    && ParseRun(time, kind, id, name, sum,
                        o["keep"] is JsonNode k ? k.GetValue<bool>() : null,
                        o["reason"]?.GetValue<string>()) is { } run)
                    runs.Add(run);
            }
            catch
            {
                // 单行损坏跳过，不影响其余历史。
            }
        }
        return runs
            .OrderBy(r => r.Time == DateTime.MinValue ? DateTime.MinValue : r.Time)
            .ToList();
    }

    private static ExperimentRun? ParseRun(
        DateTime time, string kind, string id, string name,
        JsonObject summary, bool? keep, string? reason)
    {
        var avg = summary["avgFps"] is JsonNode a ? a.GetValue<double>() : double.NaN;
        var p1 = summary["p1Low"] is JsonNode p ? p.GetValue<double>() : double.NaN;
        if (double.IsNaN(avg) || double.IsNaN(p1))
            return null;
        return new ExperimentRun(time, kind, id, string.IsNullOrWhiteSpace(name) ? id : name, avg, p1, keep, reason);
    }

    private static string[] ReadAllLinesSafe(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            return File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            return [];
        }
    }
}
