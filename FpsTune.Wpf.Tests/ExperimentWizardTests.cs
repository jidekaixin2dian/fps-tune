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
        // 报告在基线完成后即允许
        Assert.Null(ExperimentWizard.CanRunStep(state, "report"));
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
        ExperimentWizardStore.Save(state);

        var loaded = ExperimentWizardStore.Load();
        Assert.True(loaded.BaselineDone);
        Assert.Equal(100, loaded.BaselineAvgFps);
        Assert.Single(loaded.Groups);
        Assert.Equal("sid", loaded.Groups[0].SessionId);
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
}
