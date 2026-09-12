using System.Text.Json;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>A/B 实验向导：合法/非法转换、重入、恢复、旧状态迁移与持久化。</summary>
public sealed class ExperimentWizardTests : IDisposable
{
    private readonly string _dir;

    public ExperimentWizardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fpstune-wizard-tests-" + Guid.NewGuid().ToString("N"));
        ExperimentWizardStore.OverrideDir = _dir;
    }

    public void Dispose()
    {
        ExperimentWizardStore.OverrideDir = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static ExperimentWizardState BaselineDoneState(bool stable = true)
        => ExperimentWizard.WithBaseline(new ExperimentWizardState(), 100, 55, 0.01, stable, null);

    // ---------- 状态机转换 ----------

    [Fact]
    public void Group_steps_blocked_until_baseline_done()
    {
        var state = new ExperimentWizardState();
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "group-1"));
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "report"));
        Assert.Null(ExperimentWizard.CanRunStep(state, "baseline"));
    }

    [Fact]
    public void Unstable_baseline_blocks_groups_but_not_baseline_rerun()
    {
        var state = BaselineDoneState(stable: false);
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "group-1"));
        Assert.Null(ExperimentWizard.CanRunStep(state, "baseline"));
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "report"));
    }

    [Fact]
    public void Groups_must_run_in_order()
    {
        var state = BaselineDoneState();
        Assert.Null(ExperimentWizard.CanRunStep(state, "group-1"));
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "group-2"));
        var afterG1 = ExperimentWizard.WithGroupResult(state,
            new WizardGroupResult("group-1", true, false, "ok", 105, 56, null, false, DateTime.Now), null);
        Assert.Null(ExperimentWizard.CanRunStep(afterG1, "group-2"));
        Assert.NotNull(ExperimentWizard.CanRunStep(afterG1, "group-3"));
    }

    [Fact]
    public void Running_step_blocks_everything_and_error_allows_retry()
    {
        var state = ExperimentWizard.WithRunning(BaselineDoneState(), "group-1");
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "baseline"));
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "group-1"));
        var errored = ExperimentWizard.WithError(state, "应用候选组失败");
        Assert.Null(ExperimentWizard.CanRunStep(errored, "group-1")); // 失败后可重试
        Assert.Equal("group-1", errored.LastErrorStep);
    }

    [Fact]
    public void Report_requires_all_three_groups_and_exposes_next_step()
    {
        var state = BaselineDoneState();
        Assert.Equal(new[] { "baseline" }, state.CompletedSteps);
        Assert.Equal("group-1", state.NextStep);
        Assert.NotNull(ExperimentWizard.CanRunStep(state, "report"));

        foreach (var group in WizardSteps.Groups)
        {
            state = ExperimentWizard.WithGroupResult(state,
                new WizardGroupResult(group, true, false, "ok", 100, 55, null, true, DateTime.Now), null);
        }

        Assert.Equal(WizardSteps.Report, state.NextStep);
        Assert.Equal(WizardSteps.AllSteps[..4], state.CompletedSteps);
        Assert.Null(ExperimentWizard.CanRunStep(state, WizardSteps.Report));
        Assert.Equal(WizardSteps.Report, state.CurrentStep);
    }

    [Fact]
    public void Successful_retry_clears_error_and_report_is_invalidated()
    {
        var state = BaselineDoneState();
        state = ExperimentWizard.WithRunning(state, "group-1");
        state = ExperimentWizard.WithError(state, "脚本失败");
        state = ExperimentWizard.WithGroupResult(state,
            new WizardGroupResult("group-1", true, false, "重试成功", 105, 56, null, true, DateTime.Now), null);
        Assert.Null(state.LastError);
        Assert.Null(state.LastErrorStep);
        Assert.False(state.ReportGenerated);

        state = ExperimentWizard.WithReportGenerated(state);
        state = ExperimentWizard.WithGroupResult(state,
            new WizardGroupResult("group-1", true, false, "再次测试", 106, 57, null, true, DateTime.Now), null);
        Assert.False(state.ReportGenerated);
        Assert.Null(state.ReportAt);
    }

    [Fact]
    public void Recollecting_baseline_discards_stale_groups_and_report()
    {
        var state = BaselineDoneState();
        state = ExperimentWizard.WithGroupResult(state,
            new WizardGroupResult("group-1", true, false, "ok", 105, 56, "session", true, DateTime.Now), "session");
        state = ExperimentWizard.WithReportGenerated(state);
        state = ExperimentWizard.WithBaseline(state, 90, 50, 0.02, true, null);
        Assert.Empty(state.Groups);
        Assert.False(state.ReportGenerated);
        Assert.Null(state.BaselineSessionId);
        Assert.Equal("group-1", state.NextStep);
    }

    [Fact]
    public void Unknown_step_is_rejected()
        => Assert.NotNull(ExperimentWizard.CanRunStep(new ExperimentWizardState(), "group-9"));

    [Fact]
    public void Repeating_a_group_replaces_its_result_only()
    {
        var state = BaselineDoneState();
        state = ExperimentWizard.WithGroupResult(state,
            new WizardGroupResult("group-1", true, false, "第一轮", 105, 56, null, false, DateTime.Now), null);
        state = ExperimentWizard.WithGroupResult(state,
            new WizardGroupResult("group-1", false, true, "第二轮", 101, 54, "sess-1", false, DateTime.Now), "sess-1");
        Assert.Single(state.Groups);
        Assert.False(state.Groups[0].Keep);
        Assert.Equal("第二轮", state.Groups[0].Reason);
        Assert.Equal("sess-1", state.Groups[0].SessionId);
    }

    // ---------- 持久化与中断恢复 ----------

    [Fact]
    public void Store_roundtrip_preserves_state()
    {
        var state = BaselineDoneState();
        state = ExperimentWizard.WithGroupResult(state,
            new WizardGroupResult("group-1", true, false, "ok", 106, 57, "sid", true, DateTime.Now), "sid");
        state = ExperimentWizard.WithRawOutput(state, "{\"mode\":\"test\"}");
        ExperimentWizardStore.Save(state);

        var loaded = ExperimentWizardStore.Load();
        Assert.True(loaded.BaselineDone);
        Assert.Equal(100, loaded.BaselineAvgFps);
        Assert.Single(loaded.Groups);
        Assert.Equal("sid", loaded.Groups[0].SessionId);
        Assert.Equal("{\"mode\":\"test\"}", loaded.LastRawOutput);
    }

    [Fact]
    public void Store_recovers_interrupted_running_step()
    {
        var state = ExperimentWizard.WithRunning(BaselineDoneState(), "group-2");
        ExperimentWizardStore.Save(state);
        var loaded = ExperimentWizardStore.Load();
        Assert.Null(loaded.RunningStep);
        Assert.NotNull(loaded.LastError);
        Assert.Contains("未完成", loaded.LastError);
        Assert.True(loaded.BaselineDone, "已完成步骤不因中断丢失");
    }

    [Fact]
    public void Store_tolerates_corrupt_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "wizard.json"), "{broken");
        var loaded = ExperimentWizardStore.Load(); // 不抛异常
        Assert.NotNull(loaded);
        Assert.True(File.Exists(Path.Combine(_dir, "wizard.json.corrupt")));
    }

    // ---------- 旧 state.json 迁移 ----------

    [Fact]
    public void Migration_reads_legacy_state_json()
    {
        Directory.CreateDirectory(_dir);
        var legacy = new
        {
            schema = "v1",
            updatedAt = "2026-09-01T10:00:00Z",
            baseline = new
            {
                samples = new object[0],
                summary = new { avgFps = 99.5, p1Low = 54.0, p99Ms = 11.0, stutters = 7, cv = 0.02, stable = true },
                mode = "simulated",
                durationSec = 90
            },
            groups = new object[]
            {
                new
                {
                    id = "group-1", name = "调度组", items = new[] { "mmcss-games" },
                    appliedAt = "2026-09-01T11:00:00Z",
                    summary = new { avgFps = 105.0, p1Low = 56.0, p99Ms = 10.5, stutters = 4, cv = 0.03, stable = true },
                    keep = true, reverted = false, reason = "平均帧率提升 5.5%", samplerMode = "simulated"
                }
            }
        };
        File.WriteAllText(Path.Combine(_dir, "state.json"),
            JsonSerializer.Serialize(legacy));

        var migrated = ExperimentWizardStore.Load();
        Assert.True(migrated.BaselineDone);
        Assert.Equal(99.5, migrated.BaselineAvgFps);
        Assert.Single(migrated.Groups);
        Assert.Equal("group-1", migrated.Groups[0].GroupId);
        Assert.True(migrated.Groups[0].Keep);
    }

    [Fact]
    public void Migration_returns_null_without_legacy_state()
    {
        var migrated = ExperimentWizardStore.MigrateFromLegacyState();
        Assert.Null(migrated);
    }

    [Fact]
    public void Migration_recovers_latest_results_from_history_when_state_is_missing()
    {
        Directory.CreateDirectory(_dir);
        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                time = "2026-09-01T10:00:00Z", kind = "baseline", id = "baseline", name = "基线",
                summary = new { avgFps = 99.0, p1Low = 54.0, cv = 0.02, stable = true }
            }),
            "{not-json}",
            JsonSerializer.Serialize(new
            {
                time = "2026-09-01T11:00:00Z", kind = "test", id = "group-1", name = "调度组",
                summary = new { avgFps = 105.0, p1Low = 57.0 }, keep = true,
                reason = "收益", samplerMode = "simulated"
            })
        };
        File.WriteAllLines(Path.Combine(_dir, "history.jsonl"), lines);

        var loaded = ExperimentWizardStore.Load();
        Assert.True(loaded.BaselineDone);
        Assert.Equal(99.0, loaded.BaselineAvgFps);
        Assert.Single(loaded.Groups);
        Assert.Equal("group-1", loaded.Groups[0].GroupId);
        Assert.True(loaded.Groups[0].Keep);
        Assert.True(File.Exists(ExperimentWizardStore.WizardFile));
    }

    [Fact]
    public void Store_normalizes_null_and_unknown_groups_without_throwing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ExperimentWizardStore.WizardFile, "{\"groups\":null,\"runningStep\":\"bogus\"}");

        var loaded = ExperimentWizardStore.Load();
        Assert.Empty(loaded.Groups);
        Assert.Null(loaded.RunningStep);
        Assert.Equal(WizardSteps.Baseline, loaded.NextStep);
    }

    [Fact]
    public void Future_schema_is_read_only_and_file_bytes_are_preserved()
    {
        Directory.CreateDirectory(_dir);
        var json = "{\n  \"schemaVersion\": 99,\n  \"runningStep\": \"group-2\",\n  \"futureField\": { \"keep\": true }\n}\n";
        File.WriteAllText(ExperimentWizardStore.WizardFile, json);
        var before = File.ReadAllBytes(ExperimentWizardStore.WizardFile);

        var loaded = ExperimentWizardStore.Load();

        Assert.Equal(99, loaded.SchemaVersion);
        Assert.True(loaded.IsFutureSchema);
        Assert.Equal("group-2", loaded.RunningStep);
        Assert.Contains("仅支持查看", loaded.CompatibilityError);
        Assert.NotNull(ExperimentWizard.CanRunStep(loaded, WizardSteps.Baseline));
        Assert.False(ExperimentWizardStore.TrySave(loaded, out var error));
        Assert.Contains("高于当前版本", error);
        Assert.Equal(before, File.ReadAllBytes(ExperimentWizardStore.WizardFile));
    }

    [Fact]
    public void PowerShell_failure_text_redacts_script_and_wrapper_paths()
    {
        var script = Path.Combine(_dir, "friend-test.ps1");
        var wrapper = Path.Combine(Path.GetTempPath(), "delta-tune-wpf-tmp", "run.ps1");
        var text = $"At {script}:4 char:1\nwrapper={wrapper}";

        var safe = PowerShellRunner.SanitizeProcessText(text, script, wrapper);

        Assert.DoesNotContain(script, safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(wrapper, safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<redacted-path>", safe);
    }
}
