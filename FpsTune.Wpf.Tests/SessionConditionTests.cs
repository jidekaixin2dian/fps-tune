using System.Text.Json;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class SessionConditionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-conditions-" + Guid.NewGuid().ToString("N"));
    private readonly string? _game = AppState.GamePath;
    public SessionConditionTests() { PerformanceSessionStore.OverrideDir = _root; AppState.GamePath = null; }
    public void Dispose() { PerformanceSessionStore.OverrideDir = null; AppState.GamePath = _game; if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    internal static PerformanceSession Example(string name = "Baseline")
    {
        var start = new DateTime(2026, 10, 14, 12, 0, 0);
        return new(PerformanceSessionStore.NewSessionId(), name, start, start.AddSeconds(60), 1, 1,
            [new(start, 20, 40, 80, 3000)], Fps: new SessionFpsStats(3600, 120, 90, 12, 0),
            Conditions: new SessionConditions(new string('A', 64), "Game", 1920, 1080, "High / DLSS Quality / uncapped", "Training route", 60));
    }
    [Fact]
    public void Legacy_records_remain_readable_and_unknown_conditions_are_visible()
    {
        var old = Example() with { Conditions = null };
        PerformanceSessionStore.Save(old);
        Assert.Null(PerformanceSessionStore.LoadAll().Single().Conditions);
        Assert.Contains(SessionConditionRules.Differences(old, Example()), s => s == Str.T("Str.ConditionMissing", Str.T("Str.ConditionScene")));
    }
    [Fact]
    public void Conditions_survive_history_active_snapshot_and_interruption_recovery()
    {
        var session = Example();
        PerformanceSessionStore.Save(session);
        Assert.Equal(session.Conditions, PerformanceSessionStore.LoadHistory().Single().Header.Conditions);
        PerformanceSessionStore.SaveActiveSnapshot(session with { Id = "active-snapshot" });
        var recovered = PerformanceSessionStore.RecoverInterruptedSession(minSamples: 1)!;
        Assert.Equal(session.Conditions, recovered.Conditions);
        Assert.False(PerformanceSessionStore.HasActiveSnapshot);
    }
    [Theory]
    [InlineData(1920, null, 60)]
    [InlineData(0, 1080, 60)]
    [InlineData(1920, 1080, 0)]
    [InlineData(1920, 1080, 14401)]
    public void Invalid_metadata_is_rejected_before_persistence(int width, int? height, int seconds)
    {
        var invalid = Example() with { Conditions = new SessionConditions(Width: width, Height: height, TargetSeconds: seconds) };
        Assert.Throws<ArgumentException>(() => PerformanceSessionStore.Save(invalid));
        Assert.False(Directory.Exists(_root));
    }
    [Fact]
    public void Comparison_reports_game_resolution_scene_interval_and_duration_mismatches_without_changing_stats()
    {
        var a = Example(); var b = Example("After");
        Assert.Empty(SessionConditionRules.Differences(a, b));
        b = b with { Conditions = b.Conditions! with { GameKey = new string('B', 64), Width = 2560, Scene = "Different route" },
            IntervalSeconds = 3, EndedAt = b.StartedAt.AddSeconds(35), Fps = null };
        var issues = SessionConditionRules.Differences(a, b);
        Assert.Contains(Str.T("Str.ConditionDifferent", Str.T("Str.ConditionGame")), issues);
        Assert.Contains(Str.T("Str.ConditionDifferent", Str.T("Str.ConditionResolution")), issues);
        Assert.Contains(Str.T("Str.ConditionDifferent", Str.T("Str.ConditionScene")), issues);
        Assert.Contains(Str.T("Str.ConditionIntervalDiff"), issues);
        Assert.Contains(Str.T("Str.ConditionDurationDiff"), issues);
        Assert.Contains(Str.T("Str.ConditionTargetMissed"), issues);
        Assert.Contains(Str.T("Str.ConditionFpsMissing"), issues);
        Assert.Equal(120, a.Fps!.AvgFps);
    }
    [Fact]
    public void Capture_has_stable_path_identity_and_different_games_do_not_share_it()
    {
        var c = new SessionConditions(Quality: " High ", Scene: " Route ");
        var a = SessionConditionRules.CaptureGame(@"C:\Games\one\game.exe", c);
        Assert.Equal(a.GameKey, SessionConditionRules.CaptureGame(@"c:\games\ONE\GAME.exe", c).GameKey);
        Assert.NotEqual(a.GameKey, SessionConditionRules.CaptureGame(@"C:\Games\two\game.exe", c).GameKey);
        Assert.Equal("High", a.Quality);
        Assert.DoesNotContain("C:", JsonSerializer.Serialize(a));
    }
    [Fact]
    public void Running_session_keeps_the_sampler_interval_when_low_spec_mode_changes()
    {
        var previous = UiPerformance.LowSpec;
        using var service = new PerformanceSessionService();
        try
        {
            UiPerformance.LowSpec = false;
            Assert.True(service.Start("Fixed interval", new SessionConditions(Scene: "Route")));
            UiPerformance.LowSpec = true;
            var saved = service.Stop()!;
            Assert.Equal(1, saved.IntervalSeconds);
            Assert.Equal("Route", saved.Conditions!.Scene);
        }
        finally { UiPerformance.LowSpec = previous; }
    }
    [Fact]
    public void Export_keeps_conditions_scrubs_paths_and_neutralizes_csv_formulas()
    {
        var session = Example() with { Conditions = Example().Conditions! with { Scene = @"C:\Users\Someone\secret scene", Quality = "=1+1,\"High\"" } };
        var json = SessionExporter.BuildJson(session);
        Assert.DoesNotContain("Someone", json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1920, doc.RootElement.GetProperty("session").GetProperty("Conditions").GetProperty("Width").GetInt32());
        var csv = SessionExporter.BuildCsv(session);
        Assert.Contains(",target_seconds,interval_seconds,duration_seconds", csv);
        Assert.Contains("\"'=1+1,\"\"High\"\"\"", csv);
        Assert.DoesNotContain("Someone", csv);
        Assert.EndsWith(",60,1,60\n", csv);
    }
}
