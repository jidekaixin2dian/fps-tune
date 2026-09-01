using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
/// 基线完成后随时可生成报告；同一时刻只允许一步在运行（防并发/重复点击）。
/// </summary>
public sealed record ExperimentWizardState
{
    public int SchemaVersion { get; init; } = 1;
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

    public WizardGroupResult? GroupResult(string groupId)
        => Groups.FirstOrDefault(g => string.Equals(g.GroupId, groupId, StringComparison.Ordinal));

    public bool GroupDone(string groupId) => GroupResult(groupId) is not null;
}

public static class ExperimentWizard
{
    /// <summary>步骤是否允许运行。返回 null = 允许，否则为拒绝原因。</summary>
    public static string? CanRunStep(ExperimentWizardState state, string step)
    {
        if (!WizardSteps.AllSteps.Contains(step))
            return "未知步骤：" + step;
        if (!string.IsNullOrEmpty(state.RunningStep))
            return $"步骤「{WizardSteps.DisplayName(state.RunningStep)}」正在运行，请等待完成或重启程序后重试。";
        if (state.LastError is not null && WizardSteps.IsGroup(step))
        {
            // 失败后允许重试，不阻塞；此分支仅为可读性保留
        }
        switch (step)
        {
            case WizardSteps.Baseline:
                return null;
            case WizardSteps.Report:
                return state.BaselineDone ? null : "请先完成基线采样。";
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
            BaselineSessionId = sessionId ?? state.BaselineSessionId,
            BaselineAt = DateTime.Now,
            RunningStep = null,
            RunningSince = null
        };

    public static ExperimentWizardState WithGroupResult(
        ExperimentWizardState state, WizardGroupResult result, string? sessionId)
    {
        var groups = state.Groups
            .Where(g => !string.Equals(g.GroupId, result.GroupId, StringComparison.Ordinal))
            .ToList();
        groups.Add(result with { SessionId = sessionId ?? result.SessionId });
        return state with
        {
            Groups = groups.OrderBy(g => Array.IndexOf(WizardSteps.Groups, g.GroupId)).ToList(),
            RunningStep = null,
            RunningSince = null
        };
    }

    public static ExperimentWizardState WithReportGenerated(ExperimentWizardState state)
        => state with { ReportGenerated = true, ReportAt = DateTime.Now, RunningStep = null, RunningSince = null };

    public static ExperimentWizardState WithRunning(ExperimentWizardState state, string step)
        => state with { RunningStep = step, RunningSince = DateTime.Now };

    public static ExperimentWizardState WithError(ExperimentWizardState state, string error)
        => state with { LastError = error, LastErrorAt = DateTime.Now, RunningStep = null, RunningSince = null };

    /// <summary>清空向导（新的一轮实验），保留历史（history.jsonl 不受影响）。</summary>
    public static ExperimentWizardState Reset()
        => new() { SchemaVersion = 1 };
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

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

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
                    return RecoverInterrupted(state);
            }
        }
        catch (Exception ex)
        {
            AtomicFile.PreserveCorrupt(WizardFile);
            var recovered = RecoverInterrupted(new ExperimentWizardState())
                with { LastError = "向导状态文件损坏，已从空状态恢复：" + ex.Message, LastErrorAt = DateTime.Now };
            return recovered;
        }

        // 没有向导文件：尝试从旧版 state.json 迁移
        var migrated = MigrateFromLegacyState();
        return migrated is not null ? RecoverInterrupted(migrated) : new ExperimentWizardState();
    }

    public static void Save(ExperimentWizardState state)
    {
        try
        {
            Directory.CreateDirectory(ExperimentDir);
            AtomicFile.WriteAllText(WizardFile, JsonSerializer.Serialize(state, JsonOpts), new UTF8Encoding(false));
        }
        catch
        {
            // 状态保存失败不阻塞实验流程；下次步骤完成时会重写
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
            LastErrorAt = DateTime.Now
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
}
