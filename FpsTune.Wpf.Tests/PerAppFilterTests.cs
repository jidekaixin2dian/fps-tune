using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class PerAppFilterTests : IDisposable
{
    private readonly string _root;
    public PerAppFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fpstune-perapp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        IccAppRuleStore.FileOverride = () => Path.Combine(_root, "rules.json");
        FilterPreferenceStore.FileOverride = () => Path.Combine(_root, "preferences.json");
        IccAppRuleStore.Invalidate();
        FilterPreferenceStore.Invalidate();
    }

    public void Dispose()
    {
        ForegroundFilterWatcher.ForegroundProcessOverride = null;
        ForegroundFilterWatcher.ApplyOverride = null;
        ForegroundFilterWatcher.RestoreOverride = null;
        ForegroundFilterWatcher.DisplayOverride = null;
        ForegroundFilterWatcher.ProcessAliveOverride = null;
        IccAppRuleStore.FileOverride = null;
        FilterPreferenceStore.FileOverride = null;
        UserDataPaths.RootOverride = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static FilterAppRule Rule(string process = "game.exe", string preset = "generated:Vivid",
        int? vibrance = 70, bool screen = true, string path = "", string title = "",
        FilterAppMatchKind kind = FilterAppMatchKind.ProcessName) =>
        new(process, preset, vibrance, screen, path, title, true, kind);

    [Fact]
    public void Path_rule_wins_over_process_rule_and_title_rule_requires_a_title()
    {
        var process = Rule("game.exe", "generated:Vivid");
        var exe = @"C:\Games\Game.exe";
        var path = Rule("game.exe", "generated:Cool", path: exe, kind: FilterAppMatchKind.Path);
        var rule = FilterAppRuleMatcher.Match([process, path], new("game.exe", exe, "Game"));
        Assert.Equal("generated:Cool", rule?.PresetFileName);
        Assert.Equal(process.Key, FilterAppRuleMatcher.Match([process, path with { TitleContains = "Launcher" }],
            new("game.exe", exe, "Game"))?.Key);
        Assert.Null(FilterAppRuleMatcher.Match([path with { TitleContains = "Launcher" }],
            new("game.exe", exe, "Game")));
    }

    [Fact]
    public void Match_is_case_insensitive_by_path_and_ignores_disabled_rules()
    {
        var path = Rule(path: @"C:\Games\Game.exe", kind: FilterAppMatchKind.Path);
        IccAppRuleStore.Save([path]);
        Assert.NotNull(IccAppRuleStore.Match(new ForegroundAppInfo("GAME.EXE", @"c:\games\GAME.EXE", "")));
        IccAppRuleStore.Save([path with { Enabled = false }]);
        Assert.Null(IccAppRuleStore.Match(new ForegroundAppInfo("GAME.EXE", @"c:\games\GAME.EXE", "")));
        Assert.Null(IccAppRuleStore.Match(new ForegroundAppInfo("other.exe", @"C:\Games\Other.exe", "")));
    }

    [Fact]
    public void Rule_store_persists_extended_fields_deduplicates_by_identity_and_enforces_limit()
    {
        var path = Path.Combine(_root, "rules.json");
        IccAppRuleStore.Save([Rule("GAME.EXE", path: "", title: "Launcher"),
            Rule("game", path: "", title: "Launcher")]);
        var loaded = Assert.Single(IccAppRuleStore.Rules);
        Assert.Equal("game.exe", loaded.ProcessName);
        Assert.Equal("Launcher", loaded.TitleContains);
        Assert.True(File.Exists(path));
        var rules = Enumerable.Range(0, IccAppRuleStore.MaxRules + 10)
            .Select(i => Rule("game" + i + ".exe")).ToList();
        IccAppRuleStore.Save(rules);
        Assert.Equal(IccAppRuleStore.MaxRules, IccAppRuleStore.Rules.Count);
    }

    [Fact]
    public void Export_round_trips_and_a_malformed_import_does_not_replace_existing_rules()
    {
        var original = Rule("game.exe", "generated:Warm", 65, false, title: "Game", kind: FilterAppMatchKind.ProcessName);
        IccAppRuleStore.Save([original]);
        var export = Path.Combine(_root, "export.json");
        IccAppRuleStore.Export(export);
        IccAppRuleStore.Save([Rule("other.exe")]);
        var imported = IccAppRuleStore.Import(export);
        Assert.Single(imported);
        Assert.Equal(original.Key, imported[0].Key);
        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "{not-json");
        Assert.ThrowsAny<Exception>(() => IccAppRuleStore.Import(bad));
        Assert.Equal(original.Key, Assert.Single(IccAppRuleStore.Rules).Key);
    }

    [Fact]
    public void State_machine_applies_after_enter_streak_restores_after_exit_streak_and_switches_immediately()
    {
        var machine = new PerAppFilterStateMachine { EnterSamples = 2, ExitSamples = 2 };
        var a = Rule("a.exe"); var b = Rule("b.exe");
        Assert.Equal(PerAppFilterAction.None, machine.Update(a, "D1").Action);
        Assert.Equal(PerAppFilterAction.Apply, machine.Update(a, "D1").Action);
        Assert.Equal(PerAppFilterAction.None, machine.Update(a, "D1").Action);
        Assert.Equal(PerAppFilterAction.None, machine.Update(b, "D1").Action);
        Assert.Equal(PerAppFilterAction.Apply, machine.Update(b, "D1").Action);
        Assert.Equal(PerAppFilterAction.None, machine.Update(b, "D1").Action);
        Assert.Equal(PerAppFilterAction.None, machine.Update(null, null).Action);
        Assert.Equal(PerAppFilterAction.Restore, machine.Update(null, null).Action);
        Assert.Equal(PerAppFilterAction.None, machine.Update(null, null).Action);
    }

    [Fact]
    public void Watcher_requires_confirmation_before_apply_and_restore_and_reports_diagnostics()
    {
        var old = SettingsService.Current.FilterWatcherEnabled;
        var oldFullscreen = SettingsService.Current.FullscreenPresetEnabled;
        IccAppRuleStore.Save([Rule("cs2.exe", "generated:Vivid", 80, true)]);
        SettingsService.Current.FilterWatcherEnabled = true;
        SettingsService.Current.FullscreenPresetEnabled = false;
        var applies = 0; var restores = 0;
        ForegroundFilterWatcher.ForegroundProcessOverride = () => new("cs2.exe", @"C:\Games\cs2.exe", "Counter-Strike 2");
        ForegroundFilterWatcher.ApplyOverride = _ => { applies++; return true; };
        ForegroundFilterWatcher.RestoreOverride = () => { restores++; return true; };
        ForegroundFilterWatcher.DisplayOverride = () => "D1";
        try
        {
            Assert.NotNull(IccAppRuleStore.Match(new ForegroundAppInfo("cs2.exe", @"C:\Games\cs2.exe", "Counter-Strike 2")));
            using var watcher = new ForegroundFilterWatcher();
            watcher.Poll();
            Assert.Equal(0, applies);
            watcher.Poll();
            var debug = watcher.Status;
            Assert.True(applies == 1, $"{debug.Detail} | {debug.LastError} | enabled={SettingsService.Current.FilterWatcherEnabled} full={SettingsService.Current.FullscreenPresetEnabled}");
            var status = watcher.Status;
            Assert.Equal("cs2.exe", status.ActiveProcess);
            Assert.Equal("generated:Vivid", status.ActivePreset);
            Assert.Equal(80, status.Vibrance);
            Assert.True(status.ScreenIncluded);
            ForegroundFilterWatcher.ForegroundProcessOverride = () => new("explorer.exe");
            watcher.Poll();
            Assert.Equal(0, restores);
            watcher.Poll();
            Assert.Equal(1, restores);
            Assert.Null(watcher.Status.ActivePreset);
        }
        finally
        {
            SettingsService.Current.FilterWatcherEnabled = old;
            SettingsService.Current.FullscreenPresetEnabled = oldFullscreen;
            SettingsService.Save(SettingsService.Current);
        }
    }

    [Fact]
    public void Watcher_restores_immediately_when_the_applied_process_exits_even_if_foreground_is_stale()
    {
        var old = SettingsService.Current.FilterWatcherEnabled;
        var oldFullscreen = SettingsService.Current.FullscreenPresetEnabled;
        SettingsService.Current.FilterWatcherEnabled = true;
        SettingsService.Current.FullscreenPresetEnabled = false;
        IccAppRuleStore.Save([Rule("game.exe", "generated:Vivid", 60, false)]);
        var restores = 0;
        ForegroundFilterWatcher.ForegroundProcessOverride = () => new("game.exe", @"C:\Games\game.exe", "Game", 4321);
        ForegroundFilterWatcher.DisplayOverride = () => "D1";
        ForegroundFilterWatcher.ApplyOverride = _ => true;
        ForegroundFilterWatcher.RestoreOverride = () => { restores++; return true; };
        try
        {
            using var watcher = new ForegroundFilterWatcher();
            watcher.Poll(); watcher.Poll();
            Assert.NotNull(watcher.Status.ActivePreset);
            ForegroundFilterWatcher.ProcessAliveOverride = _ => false; // 模拟游戏进程已退出，前台窗口仍是旧 HWND。
            watcher.Poll();
            Assert.Equal(1, restores);
            Assert.Null(watcher.Status.ActivePreset);
            Assert.Contains("FilterWatcherRestored", watcher.Status.Detail);
        }
        finally
        {
            SettingsService.Current.FilterWatcherEnabled = old;
            SettingsService.Current.FullscreenPresetEnabled = oldFullscreen;
            SettingsService.Save(SettingsService.Current);
        }
    }

    [Fact]
    public void Fullscreen_switch_suppresses_per_app_rules_without_reporting_a_fake_match()
    {
        var oldFull = SettingsService.Current.FullscreenPresetEnabled;
        var oldWatch = SettingsService.Current.FilterWatcherEnabled;
        SettingsService.Current.FilterWatcherEnabled = true;
        SettingsService.Current.FullscreenPresetEnabled = true;
        ForegroundFilterWatcher.ForegroundProcessOverride = () => new("game.exe");
        ForegroundFilterWatcher.ApplyOverride = _ => throw new InvalidOperationException("must not apply");
        try
        {
            using var watcher = new ForegroundFilterWatcher();
            watcher.Poll();
            var status = watcher.Status;
            Assert.True(status.Suppressed);
            Assert.Null(status.ActivePreset);
            Assert.Contains("FilterWatcherSuppressed", status.Detail);
        }
        finally
        {
            SettingsService.Current.FullscreenPresetEnabled = oldFull;
            SettingsService.Current.FilterWatcherEnabled = oldWatch;
            SettingsService.Save(SettingsService.Current);
        }
    }

    [Fact]
    public void Watcher_retries_the_apply_after_a_failed_write_instead_of_giving_up_for_the_session()
    {
        // 回归：Apply 失败进退避后，状态机在退避期内把决策重新提交（_current=rule），
        // 后续 Update 恒为 None——这一局游戏里滤镜再也不套。修复后退避期内决策被退回，
        // 退避到期重新去抖再试。
        var old = SettingsService.Current.FilterWatcherEnabled;
        var oldFullscreen = SettingsService.Current.FullscreenPresetEnabled;
        var oldBackoff = ForegroundFilterWatcher.RetryBackoff;
        IccAppRuleStore.Save([Rule("cs2.exe", "generated:Vivid", 80, true)]);
        SettingsService.Current.FilterWatcherEnabled = true;
        SettingsService.Current.FullscreenPresetEnabled = false;
        ForegroundFilterWatcher.RetryBackoff = TimeSpan.FromMilliseconds(300);
        var applies = 0;
        ForegroundFilterWatcher.ForegroundProcessOverride = () => new("cs2.exe", @"C:\Games\cs2.exe", "Counter-Strike 2");
        ForegroundFilterWatcher.DisplayOverride = () => "D1";
        ForegroundFilterWatcher.ApplyOverride = _ =>
        {
            applies++;
            return applies > 1; // 第一次失败，之后成功
        };
        ForegroundFilterWatcher.RestoreOverride = () => true;
        try
        {
            using var watcher = new ForegroundFilterWatcher();
            watcher.Poll(); watcher.Poll();
            Assert.Equal(1, applies); // 第一次尝试失败

            watcher.Poll(); watcher.Poll(); // 退避期内：决策被退回，不再尝试
            Assert.Equal(1, applies);
            Assert.Null(watcher.Status.ActivePreset);

            Thread.Sleep(350); // 退避到期 → 重新去抖 → 第二次尝试成功
            watcher.Poll(); watcher.Poll();
            Assert.Equal(2, applies);
            Assert.Equal("generated:Vivid", watcher.Status.ActivePreset);
        }
        finally
        {
            ForegroundFilterWatcher.RetryBackoff = oldBackoff;
            SettingsService.Current.FilterWatcherEnabled = old;
            SettingsService.Current.FullscreenPresetEnabled = oldFullscreen;
            SettingsService.Save(SettingsService.Current);
        }
    }

    [Fact]
    public void Watcher_retries_the_restore_after_a_failed_restore_once_backoff_expires()
    {
        // 回归：还原失败后 catch 恢复了 _applied/_appliedRule/_appliedDisplay 却清掉 _appliedPid，
        // 且退避到期后没有任何重试入口（只有暂停/关闭分支消费 _nextRetryAt）——
        // 切焦点路径的还原一旦失败就再也不会试，滤镜一直留在桌面上。
        var old = SettingsService.Current.FilterWatcherEnabled;
        var oldFullscreen = SettingsService.Current.FullscreenPresetEnabled;
        var oldBackoff = ForegroundFilterWatcher.RetryBackoff;
        IccAppRuleStore.Save([Rule("game.exe", "generated:Vivid", 60, false)]);
        SettingsService.Current.FilterWatcherEnabled = true;
        SettingsService.Current.FullscreenPresetEnabled = false;
        ForegroundFilterWatcher.RetryBackoff = TimeSpan.FromMilliseconds(300);
        var restores = 0;
        ForegroundFilterWatcher.ForegroundProcessOverride = () => new("game.exe", @"C:\Games\game.exe", "Game", 4321);
        ForegroundFilterWatcher.DisplayOverride = () => "D1";
        ForegroundFilterWatcher.ApplyOverride = _ => true;
        ForegroundFilterWatcher.ProcessAliveOverride = _ => true; // 原进程存活：退出边沿重试不参与，只测退避重试
        ForegroundFilterWatcher.RestoreOverride = () =>
        {
            restores++;
            return restores == 1 ? throw new InvalidOperationException("restore flaky") : true;
        };
        try
        {
            using var watcher = new ForegroundFilterWatcher();
            watcher.Poll(); watcher.Poll();
            Assert.NotNull(watcher.Status.ActivePreset);

            ForegroundFilterWatcher.ForegroundProcessOverride = () => new("explorer.exe");
            watcher.Poll(); watcher.Poll(); // 去抖 → 还原第一次失败
            Assert.Equal(1, restores);
            Assert.NotNull(watcher.Status.ActivePreset); // 还原失败仍视为已接管

            watcher.Poll(); // 退避期内不重试
            Assert.Equal(1, restores);

            Thread.Sleep(350);
            watcher.Poll(); // 退避到期 → 自动重试还原成功
            Assert.Equal(2, restores);
            Assert.Null(watcher.Status.ActivePreset);
        }
        finally
        {
            ForegroundFilterWatcher.RetryBackoff = oldBackoff;
            SettingsService.Current.FilterWatcherEnabled = old;
            SettingsService.Current.FullscreenPresetEnabled = oldFullscreen;
            SettingsService.Save(SettingsService.Current);
        }
    }

    [Fact]
    public void Preference_store_toggles_favorites_and_ranks_favorites_before_recent()
    {
        Assert.True(FilterPreferenceStore.ToggleFavorite("a.icc"));
        FilterPreferenceStore.NoteUsed("b.icc");
        FilterPreferenceStore.NoteUsed("a.icc");
        var ranked = FilterPreferenceStore.Rank([new("b.icc"), new("a.icc")]).Select(x => x.FileName).ToArray();
        Assert.True(ranked.SequenceEqual(new[] { "a.icc", "b.icc" }));
        Assert.False(FilterPreferenceStore.ToggleFavorite("a.icc"));
        Assert.Empty(FilterPreferenceStore.Favorites);
    }
}
