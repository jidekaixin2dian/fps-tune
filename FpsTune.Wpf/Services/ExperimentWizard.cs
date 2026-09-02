using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FpsTune.Wpf.Services;

/// <summary>向导步骤标识：baseline / group-1 / group-2 / group-3 / report。</summary>
public static class WizardSteps
{
    public const string Baseline = "baseline";
    public const string Report = "report";
    public static readonly string[] Groups = ["group-1", "group-2", "group-3"];
    public static readonly string[] AllSteps = ["baseline", "group-1", "group-2", "group-3", "report"];

    public static string DisplayName(string step) => step switch
    {
        Baseline => "基线采样",
        Report => "生成报告",
        _ => "候选组 " + step.Replace("group-", "G", StringComparison.Ordinal)
    };

    public static bool IsGroup(string step) => step.StartsWith("group-", StringComparison.Ordinal);
}

/// <summary>一个候选组步骤的持久化结果。</summary>
public sealed record WizardGroupResult(
    string GroupId,
    bool? Keep,
    bool Reverted,
    string Reason,
    double? AvgFps,
    double? P1Low,
    string? SessionId,
    bool Simulated,
    DateTime CompletedAt);

/// <summary>
/// A/B 实验向导的持久化状态机（schemaVersion=1，experiment/wizard.json）。
/// 规则：基线必须先完成；三个候选组按序执行（可重复运行覆盖本组结果）；
/// 三个候选组全部完成后才可生成报告；同一时刻只允许一步在运行（防并发/重复点击）。
/// </summary>
public sealed record ExperimentWizardState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public bool BaselineDone { get; init; }
    public bool BaselineStable { get; init; }
    public double? BaselineAvgFps { get; init; }
    public double? BaselineP1Low { get; init; }
    public double? BaselineCv { get; init; }
    public string? BaselineSessionId { get; init; }
    public DateTime? BaselineAt { get; init; }
    public List<WizardGroupResult> Groups { get; init; } = [];
    public bool ReportGenerated { get; init; }
    public DateTime? ReportAt { get; init; }
    /// <summary>运行中的步骤；正常保存时应为 null，非 null 表示上次进程异常退出。</summary>
    public string? RunningStep { get; init; }
    public DateTime? RunningSince { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorAt { get; init; }
    /// <summary>最近失败/中断的步骤，供恢复页明确指出可重试对象。</summary>
    public string? LastErrorStep { get; init; }
    /// <summary>最近一次脚本输出，限制长度后持久化，便于重启后排查而不无限增长。</summary>
    public string? LastRawOutput { get; init; }

    [JsonIgnore]
    public bool IsFutureSchema => SchemaVersion > CurrentSchemaVersion;

    [JsonIgnore]
    public string CompatibilityError =>
        $"向导状态版本 {SchemaVersion} 高于当前版本 {CurrentSchemaVersion}，当前仅支持查看；请使用更新版本继续实验。";

    public WizardGroupResult? GroupResult(string groupId)
        => (Groups ?? []).FirstOrDefault(g => string.Equals(g.GroupId, groupId, StringComparison.Ordinal));

    public bool GroupDone(string groupId) => GroupResult(groupId) is not null;

    /// <summary>已完成的合法步骤，供 UI/诊断使用；计算属性不改变旧 state.json 格式。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> CompletedSteps
    {
        get
        {
            var completed = new List<string>();
            if (BaselineDone)
                completed.Add(WizardSteps.Baseline);
            foreach (var group in WizardSteps.Groups)
            {
                if (GroupDone(group))
                    completed.Add(group);
            }
            if (ReportGenerated)
                completed.Add(WizardSteps.Report);
            return completed;
        }
    }

    /// <summary>按合法顺序应执行的下一步；全部完成时为 null。</summary>
    [JsonIgnore]
    public string? NextStep
    {
        get
        {
            if (!BaselineDone || !BaselineStable)
                return WizardSteps.Baseline;
            foreach (var group in WizardSteps.Groups)
            {
                if (!GroupDone(group))
                    return group;
            }
            return ReportGenerated ? null : WizardSteps.Report;
        }
    }

    /// <summary>当前需要关注的步骤：运行中优先，其次是失败重试步骤，再次为下一步。</summary>
    [JsonIgnore]
    public string? CurrentStep => RunningStep ?? LastErrorStep ?? NextStep;
}

public static class ExperimentWizard
{
    /// <summary>步骤是否允许运行。返回 null = 允许，否则为拒绝原因。</summary>
    public static string? CanRunStep(ExperimentWizardState state, string step)
    {
        if (state is null)
            return "向导状态不可用，请重新加载后重试。";
        if (!WizardSteps.AllSteps.Contains(step))
            return "未知步骤：" + step;
        if (state.IsFutureSchema)
            return state.CompatibilityError;
        if (!string.IsNullOrEmpty(state.RunningStep))
            return $"步骤「{WizardSteps.DisplayName(state.RunningStep)}」正在运行，请等待完成或重启程序后重试。";
        switch (step)
        {
            case WizardSteps.Baseline:
                return null;
            case WizardSteps.Report:
                if (!state.BaselineDone)
                    return "请先完成基线采样。";
                if (!state.BaselineStable)
                    return "基线稳定性不足，请先重新采集基线。";
                foreach (var group in WizardSteps.Groups)
                {
                    if (!state.GroupDone(group))
                        return $"请先完成 {WizardSteps.DisplayName(group)}，再生成报告。";
                }
                return null;
            default:
                if (!state.BaselineDone)
                    return "请先完成基线采样，再测试候选组。";
                if (!state.BaselineStable)
                    return "基线稳定性不足（CV > 0.05），请重新采集基线后再测试候选组。";
                // 候选组按序：前面的组必须已完成
                foreach (var g in WizardSteps.Groups)
                {
                    if (g == step)
                        return null;
                    if (!state.GroupDone(g))
                        return $"请先完成 {WizardSteps.DisplayName(g)}，再运行 {WizardSteps.DisplayName(step)}。";
                }
                return null;
        }
    }

    /// <summary>基线完成后的状态更新。</summary>
    public static ExperimentWizardState WithBaseline(
        ExperimentWizardState state, double avgFps, double p1Low, double cv, bool stable, string? sessionId)
        => state with
        {
            BaselineDone = true,
            BaselineStable = stable,
            BaselineAvgFps = avgFps,
            BaselineP1Low = p1Low,
            BaselineCv = cv,
            BaselineSessionId = sessionId,
            BaselineAt = DateTime.Now,
            // 基线变化会使所有候选组和旧报告失去比较基准，必须从 group-1 重新开始。
            Groups = [],
            ReportGenerated = false,
            ReportAt = null,
            RunningStep = null,
            RunningSince = null,
            LastError = null,
            LastErrorAt = null,
            LastErrorStep = null
        };

    public static ExperimentWizardState WithGroupResult(
        ExperimentWizardState state, WizardGroupResult result, string? sessionId)
    {
        var groups = (state.Groups ?? [])
            .Where(g => !string.Equals(g.GroupId, result.GroupId, StringComparison.Ordinal))
            .ToList();
        groups.Add(result with { SessionId = sessionId });
        return state with
        {
            Groups = groups.OrderBy(g => Array.IndexOf(WizardSteps.Groups, g.GroupId)).ToList(),
            ReportGenerated = false,
            ReportAt = null,
            RunningStep = null,
            RunningSince = null,
            LastError = null,
            LastErrorAt = null,
            LastErrorStep = null
        };
    }

    public static ExperimentWizardState WithReportGenerated(ExperimentWizardState state)
        => state with
        {
            ReportGenerated = true,
            ReportAt = DateTime.Now,
            RunningStep = null,
            RunningSince = null,
            LastError = null,
            LastErrorAt = null,
            LastErrorStep = null
        };

    public static ExperimentWizardState WithRunning(ExperimentWizardState state, string step)
        => state with { RunningStep = step, RunningSince = DateTime.Now };

    public static ExperimentWizardState WithError(ExperimentWizardState state, string error)
        => WithError(state, state.RunningStep ?? state.LastErrorStep, error);

    public static ExperimentWizardState WithError(ExperimentWizardState state, string? step, string error)
        => state with
        {
            LastError = error,
            LastErrorAt = DateTime.Now,
            LastErrorStep = WizardSteps.AllSteps.Contains(step ?? "") ? step : null,
            RunningStep = null,
            RunningSince = null
        };

    public static ExperimentWizardState WithRawOutput(ExperimentWizardState state, string? output)
        => state with
        {
            LastRawOutput = output is null
                ? null
                : output.Length <= 256_000 ? output : output[..256_000] + "\n[输出已截断]"
        };

    /// <summary>清空向导（新的一轮实验），保留历史（history.jsonl 不受影响）。</summary>
    public static ExperimentWizardState Reset()
        => new() { SchemaVersion = ExperimentWizardState.CurrentSchemaVersion };
}

/// <summary>
/// 向导状态持久化与旧数据恢复：
/// - wizard.json 损坏 → 留档并回退空状态；
/// - wizard.json 不存在但 state.json / history.jsonl 有数据 → 从旧状态迁移；
/// - RunningStep 残留 → 标记为中断错误并清空，步骤可重新运行。
/// </summary>
public static class ExperimentWizardStore
{
    internal static string? OverrideDir { get; set; }

    private static string ExperimentDir => OverrideDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune", "experiment");

    public static string WizardFile => Path.Combine(ExperimentDir, "wizard.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 兼容早期 wizard.json 的 PascalCase 与文档/外部工具常用的 camelCase。
        PropertyNameCaseInsensitive = true
    };

    /// <summary>加载向导状态（含旧数据迁移与中断恢复，绝不抛异常）。</summary>
    public static ExperimentWizardState Load()
    {
        try
        {
            if (File.Exists(WizardFile))
            {
                var json = File.ReadAllText(WizardFile, Encoding.UTF8);
                var state = JsonSerializer.Deserialize<ExperimentWizardState>(json, JsonOpts);
                if (state is not null)
                {
                    var normalized = Normalize(state);
                    if (normalized.IsFutureSchema)
                        return normalized;
                    var recoveredState = RecoverInterrupted(normalized);
                    if (!string.IsNullOrEmpty(normalized.RunningStep))
                        Save(recoveredState);
                    return recoveredState;
                }
                AtomicFile.PreserveCorrupt(WizardFile);
            }
        }
        catch (Exception ex)
        {
            AtomicFile.PreserveCorrupt(WizardFile);
            var legacyRecovered = MigrateFromLegacyState() ?? MigrateFromLegacyHistory();
            if (legacyRecovered is not null)
            {
                var recoveredState = Normalize(legacyRecovered) with
                {
                    LastError = "向导状态文件损坏，已从旧实验记录恢复：" + ex.Message,
                    LastErrorAt = DateTime.Now,
                    LastErrorStep = null
                };
                Save(recoveredState);
                return recoveredState;
            }
            return new ExperimentWizardState
            {
                LastError = "向导状态文件损坏，已从空状态恢复：" + ex.Message,
                LastErrorAt = DateTime.Now
            };
        }

        // 没有向导文件：尝试从旧版 state.json / history.jsonl 迁移
        var migrated = MigrateFromLegacyState() ?? MigrateFromLegacyHistory();
        if (migrated is null)
            return new ExperimentWizardState();
        var recovered = RecoverInterrupted(Normalize(migrated));
        Save(recovered);
        return recovered;
    }

    public static void Save(ExperimentWizardState state)
        => TrySave(state, out _);

    public static bool TrySave(ExperimentWizardState state, out string? error)
    {
        if (state.IsFutureSchema)
        {
            error = state.CompatibilityError;
            return false;
        }
        try
        {
            Directory.CreateDirectory(ExperimentDir);
            AtomicFile.WriteAllText(WizardFile, JsonSerializer.Serialize(Normalize(state), JsonOpts), new UTF8Encoding(false));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>进程中断恢复：RunningStep 残留说明上次运行没有走到保存点。</summary>
    public static ExperimentWizardState RecoverInterrupted(ExperimentWizardState state)
    {
        if (string.IsNullOrEmpty(state.RunningStep))
            return state;
        return state with
        {
            RunningStep = null,
            RunningSince = null,
            LastError = $"上次运行中断：步骤「{WizardSteps.DisplayName(state.RunningStep)}」未完成，结果未知，可重新运行。",
            LastErrorAt = DateTime.Now,
            LastErrorStep = WizardSteps.AllSteps.Contains(state.RunningStep) ? state.RunningStep : null
        };
    }

    private static ExperimentWizardState Normalize(ExperimentWizardState state)
    {
        var groups = (state.Groups ?? [])
            .Where(g => g is not null && WizardSteps.Groups.Contains(g.GroupId))
            .GroupBy(g => g.GroupId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .OrderBy(g => Array.IndexOf(WizardSteps.Groups, g.GroupId))
            .ToList();
        var running = WizardSteps.AllSteps.Contains(state.RunningStep ?? "") ? state.RunningStep : null;
        var errorStep = WizardSteps.AllSteps.Contains(state.LastErrorStep ?? "") ? state.LastErrorStep : null;
        if (state.IsFutureSchema)
        {
            // 不理解未来版本的字段和语义：仅把集合 null 规范成可读的空集合，
            // 保留 schemaVersion、RunningStep 和其余原始值，且调用方不得回写。
            return state with { Groups = groups };
        }
        var reportGenerated = state.ReportGenerated
            && state.BaselineDone
            && state.BaselineStable
            && WizardSteps.Groups.All(id => groups.Any(g => g.GroupId == id));
        return state with
        {
            SchemaVersion = ExperimentWizardState.CurrentSchemaVersion,
            Groups = groups,
            ReportGenerated = reportGenerated,
            ReportAt = reportGenerated ? state.ReportAt : null,
            RunningStep = running,
            LastErrorStep = errorStep
        };
    }

    /// <summary>从 tuning-experiment.ps1 的 state.json 迁移（只读旧文件，不改动）。</summary>
    internal static ExperimentWizardState? MigrateFromLegacyState()
    {
        try
        {
            var stateFile = Path.Combine(ExperimentDir, "state.json");
            if (!File.Exists(stateFile))
                return null;
            var root = JsonNode.Parse(File.ReadAllText(stateFile, Encoding.UTF8));
            if (root is not JsonObject obj)
                return null;

            var state = new ExperimentWizardState();
            if (obj["baseline"]?["summary"] is JsonObject summary)
            {
                var avg = summary["avgFps"] is JsonNode a ? a.GetValue<double>() : double.NaN;
                var p1 = summary["p1Low"] is JsonNode p ? p.GetValue<double>() : double.NaN;
                var cv = summary["cv"] is JsonNode c ? c.GetValue<double>() : 1.0;
                if (double.IsFinite(avg) && double.IsFinite(p1))
                {
                    state = ExperimentWizard.WithBaseline(state, avg, p1, cv, summary["stable"]?.GetValue<bool>() == true, null);
                    // 旧 state.json 的 baseline 没有 appliedAt，回退到文件级 updatedAt
                    var baselineAt = obj["baseline"]?["appliedAt"] is JsonNode at && DateTime.TryParse(at.GetValue<string>(), out var t)
                        ? t
                        : obj["updatedAt"] is JsonNode up && DateTime.TryParse(up.GetValue<string>(), out var t2)
                            ? t2
                            : (DateTime?)null;
                    if (baselineAt is not null)
                        state = state with { BaselineAt = baselineAt };
                }
            }

            if (obj["groups"] is JsonArray groups)
            {
                foreach (var g in groups)
                {
                    if (g is not JsonObject group
                        || group["id"]?.GetValue<string>() is not { Length: > 0 } id
                        || group["summary"] is not JsonObject sum)
                        continue;
                    var keep = group["keep"] is JsonNode k ? (bool?)k.GetValue<bool>() : null;
                    var reverted = group["reverted"]?.GetValue<bool>() ?? false;
                    var reason = group["reason"]?.GetValue<string>() ?? "";
                    var avg = sum["avgFps"] is JsonNode a ? a.GetValue<double>() : (double?)null;
                    var p1 = sum["p1Low"] is JsonNode p ? p.GetValue<double>() : (double?)null;
                    DateTime completed = DateTime.MinValue;
                    if (group["appliedAt"] is JsonNode ap && DateTime.TryParse(ap.GetValue<string>(), out var t2))
                        completed = t2;
                    state = ExperimentWizard.WithGroupResult(
                        state,
                        new WizardGroupResult(id, keep, reverted, reason, avg, p1, null, false, completed),
                        null);
                }
            }
            return state.BaselineDone || state.Groups.Count > 0 ? state : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 旧版 state.json 缺失或损坏时，从追加式 history.jsonl 恢复最近一轮可识别结果。
    /// 历史没有会话 ID，因此只恢复脚本指标与结论，不伪造会话关联。
    /// </summary>
    internal static ExperimentWizardState? MigrateFromLegacyHistory()
    {
        try
        {
            var historyFile = Path.Combine(ExperimentDir, "history.jsonl");
            if (!File.Exists(historyFile))
                return null;

            JsonObject? latestBaseline = null;
            var latestGroups = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var raw in File.ReadLines(historyFile, Encoding.UTF8))
            {
                try
                {
                    if (JsonNode.Parse(raw) is not JsonObject entry)
                        continue;
                    var kind = entry["kind"]?.GetValue<string>() ?? "";
                    if (kind == "baseline")
                        latestBaseline = entry;
                    else if (kind == "test"
                             && entry["id"]?.GetValue<string>() is { } id
                             && WizardSteps.Groups.Contains(id))
                        latestGroups[id] = entry;
                }
                catch
                {
                    // history 允许单行损坏，继续恢复其余记录。
                }
            }

            var state = new ExperimentWizardState();
            if (latestBaseline?["summary"] is JsonObject summary
                && TryGetFinite(summary, "avgFps", out var avg)
                && TryGetFinite(summary, "p1Low", out var p1))
            {
                var cv = TryGetFinite(summary, "cv", out var c) ? c : 1.0;
                var stable = summary["stable"]?.GetValue<bool>() == true;
                state = ExperimentWizard.WithBaseline(state, avg, p1, cv, stable, null);
            }

            foreach (var id in WizardSteps.Groups)
            {
                if (!latestGroups.TryGetValue(id, out var entry)
                    || entry["summary"] is not JsonObject groupSummary
                    || !TryGetFinite(groupSummary, "avgFps", out var groupAvg)
                    || !TryGetFinite(groupSummary, "p1Low", out var groupP1))
                    continue;
                var keep = entry["keep"] is JsonNode k ? k.GetValue<bool>() : (bool?)null;
                var reason = entry["reason"]?.GetValue<string>() ?? "";
                var at = DateTime.TryParse(entry["time"]?.GetValue<string>(), out var t) ? t : DateTime.MinValue;
                var simulated = entry["samplerMode"]?.GetValue<string>() == "simulated";
                state = ExperimentWizard.WithGroupResult(
                    state,
                    new WizardGroupResult(id, keep, keep == false, reason, groupAvg, groupP1, null, simulated, at),
                    null);
            }
            return state.BaselineDone || state.Groups.Count > 0 ? state : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetFinite(JsonObject obj, string key, out double value)
    {
        value = 0;
        try
        {
            value = obj[key]?.GetValue<double>() ?? double.NaN;
            return double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }
}
