using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 独立程序滤镜「全自动收编」这条线：方案库、范围判定、收编、以及守护的三个新开关
/// （切出游戏就恢复 / 停止 / 没收录也自动应用）。全部走真实实现，只把前台探针、写屏与文件换掉。
/// </summary>
[Collection("BackupService serial")]
public sealed class PerAppAutoCollectTests : IDisposable
{
    private readonly string _root;
    private readonly bool _oldWatcher;
    private readonly bool _oldFullscreen;
    private readonly bool _oldKeep;
    private readonly bool _oldNotify;
    private readonly bool _oldRestore;
    private readonly bool _oldFallback;
    private readonly string _oldScope;

    public PerAppAutoCollectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fpstune-perapp-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        IccAppRuleStore.FileOverride = () => Path.Combine(_root, "rules.json");
        FilterSchemeStore.FileOverride = () => Path.Combine(_root, "schemes.json");
        IccAppRuleStore.Invalidate();
        FilterSchemeStore.Invalidate();
        var settings = SettingsService.Current;
        _oldWatcher = settings.FilterWatcherEnabled;
        _oldFullscreen = settings.FullscreenPresetEnabled;
        _oldKeep = settings.FilterWatcherKeepAfterExit;
        _oldNotify = settings.FilterApplyNotify;
        _oldRestore = settings.FilterRestoreOnFocusLoss;
        _oldFallback = settings.FilterAutoFallbackEnabled;
        _oldScope = settings.FilterAutoScope;
        settings.FullscreenPresetEnabled = false;
    }

    public void Dispose()
    {
        ForegroundFilterWatcher.ForegroundProcessOverride = null;
        ForegroundFilterWatcher.ApplyOverride = null;
        ForegroundFilterWatcher.RestoreOverride = null;
        ForegroundFilterWatcher.DisplayOverride = null;
        ForegroundFilterWatcher.ProcessAliveOverride = null;
        PerAppAutoScopeRules.FullscreenOverride = null;
        IccAppRuleStore.FileOverride = null;
        FilterSchemeStore.FileOverride = null;
        var settings = SettingsService.Current;
        settings.FilterWatcherEnabled = _oldWatcher;
        settings.FullscreenPresetEnabled = _oldFullscreen;
        settings.FilterWatcherKeepAfterExit = _oldKeep;
        settings.FilterApplyNotify = _oldNotify;
        settings.FilterRestoreOnFocusLoss = _oldRestore;
        settings.FilterAutoFallbackEnabled = _oldFallback;
        settings.FilterAutoScope = _oldScope;
        IccAppRuleStore.Invalidate();
        FilterSchemeStore.Invalidate();
        UserDataPaths.RootOverride = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void UseForeground(ForegroundAppInfo? app) => ForegroundFilterWatcher.ForegroundProcessOverride = () => app;

    /// <summary>打开守护并把写屏/还原换成计数桩，返回两个计数（apply, restore）。</summary>
    private static (Func<int> Applies, Func<int> Restores) UseWatcher()
    {
        var applies = 0;
        var restores = 0;
        ForegroundFilterWatcher.ApplyOverride = _ => { applies++; return true; };
        ForegroundFilterWatcher.RestoreOverride = () => { restores++; return true; };
        ForegroundFilterWatcher.DisplayOverride = () => "D1";
        SettingsService.Current.FilterWatcherEnabled = true;
        return (() => applies, () => restores);
    }

    [Fact]
    public void Scheme_store_round_trips_caps_the_count_and_keeps_the_auto_template()
    {
        var screen = AppRuleScreen.FromSettings(ScreenColorSettings.ForPreset(IccFilterPreset.Vivid));
        var scheme = FilterSchemeStore.Save("柔和", "generated:Soft", 55, true, screen);
        Assert.Equal("柔和", scheme.Name);
        foreach (var name in new[] { "二", "三", "四", "五", "六", "七", "八" })
            FilterSchemeStore.Save(name, "generated:Cool", null, false, null);
        Assert.Equal(FilterSchemeStore.MaxSchemes, FilterSchemeStore.Schemes.Count);
        Assert.Throws<InvalidOperationException>(() => FilterSchemeStore.Save("九", "generated:Cool", null, false, null));
        // 同名覆盖不涨数量。
        FilterSchemeStore.Save("柔和", "generated:Warm", 40, true, null);
        Assert.Equal(FilterSchemeStore.MaxSchemes, FilterSchemeStore.Schemes.Count);
        Assert.Equal("generated:Warm", FilterSchemeStore.Find(scheme.Id)!.PresetFileName);

        FilterSchemeStore.SaveAutoTemplate("generated:Vivid", 65, true, screen);
        FilterSchemeStore.Invalidate();
        var template = FilterSchemeStore.AutoTemplate;
        Assert.NotNull(template);
        Assert.Equal("generated:Vivid", template!.PresetFileName);
        Assert.Equal(65, template.Vibrance);
        Assert.Equal(screen, template.Screen);
        Assert.True(File.Exists(Path.Combine(_root, "schemes.json")));
    }

    [Fact]
    public void Scope_rules_mark_game_folders_and_only_trust_fullscreen_for_games()
    {
        const string game = @"D:\SteamLibrary\steamapps\common\Game\game-Win64-Shipping.exe";
        const string tool = @"D:\Tools\editor\editor.exe";
        Assert.True(PerAppAutoScopeRules.IsGameDirectory(game));
        Assert.False(PerAppAutoScopeRules.IsGameDirectory(tool));
        Assert.True(PerAppAutoScopeRules.Matches(PerAppAutoScope.AnyApp, tool, isFullscreen: false));
        Assert.False(PerAppAutoScopeRules.Matches(PerAppAutoScope.GameDirectory, tool, isFullscreen: true));
        Assert.True(PerAppAutoScopeRules.Matches(PerAppAutoScope.GameDirectory, game, isFullscreen: false));
        // 全屏档：必须是游戏目录里的程序，而且真的全屏。
        Assert.True(PerAppAutoScopeRules.Matches(PerAppAutoScope.Fullscreen, game, isFullscreen: true));
        Assert.False(PerAppAutoScopeRules.Matches(PerAppAutoScope.Fullscreen, game, isFullscreen: false));
        Assert.False(PerAppAutoScopeRules.Matches(PerAppAutoScope.Fullscreen, tool, isFullscreen: true));
        // 本程序自己永远不套。
        Assert.False(PerAppAutoScopeRules.Matches(PerAppAutoScope.AnyApp, @"C:\App\FpsTune.exe", isFullscreen: false));
        Assert.Equal(PerAppAutoScope.GameDirectory, PerAppAutoScopeRules.Parse("gamedir"));
        Assert.Equal("any", PerAppAutoScopeRules.Key(PerAppAutoScope.AnyApp));
        Assert.Equal(PerAppAutoScope.Fullscreen, PerAppAutoScopeRules.Parse(null));
    }

    [Fact]
    public void Collector_adds_auto_rules_only_for_untracked_games_and_the_template_updates_them()
    {
        var manual = new FilterAppRule("mine.exe", "generated:Warm", 30, false, @"C:\Apps\mine.exe", "",
            true, FilterAppMatchKind.Path);
        IccAppRuleStore.Save([manual]);
        var games = new[]
        {
            new PerAppGameCollector.CollectedGame("游戏一", @"C:\Games\one\one.exe", false),
            new PerAppGameCollector.CollectedGame("已收录", @"C:\Apps\mine.exe", true),
        };
        var added = PerAppGameCollector.Collect(games, new FilterAutoTemplate("generated:Vivid", 70, true, null));
        Assert.Equal(1, added);
        var rule = Assert.Single(IccAppRuleStore.Rules, r => r.Auto);
        Assert.Equal("generated:Vivid", rule.PresetFileName);
        Assert.Equal(70, rule.Vibrance);
        Assert.Equal(1, PerAppGameCollector.CollectedCount(IccAppRuleStore.Rules));

        var updated = PerAppGameCollector.ApplyTemplate(
            new FilterAutoTemplate("generated:Cool", 45, false, AppRuleScreen.FromSettings(new ScreenColorSettings(Gamma: 1.3))));
        Assert.Equal(1, updated);
        var refreshed = IccAppRuleStore.Rules;
        var auto = Assert.Single(refreshed, r => r.Auto);
        Assert.Equal("generated:Cool", auto.PresetFileName);
        Assert.Equal(45, auto.Vibrance);
        Assert.NotNull(auto.Screen);
        // 手动规则一条都不许动。
        Assert.Equal("generated:Warm", Assert.Single(refreshed, r => !r.Auto).PresetFileName);
    }

    [Fact]
    public void Rule_store_round_trips_auto_flag_and_screen_snapshot_and_drops_a_bad_snapshot()
    {
        var screen = AppRuleScreen.FromSettings(ScreenColorSettings.ForPreset(IccFilterPreset.Bright));
        IccAppRuleStore.Save([new FilterAppRule("auto.exe", "generated:Bright", 40, true, @"C:\Games\auto.exe", "",
            true, FilterAppMatchKind.Path, Auto: true, Screen: screen)]);
        var loaded = Assert.Single(IccAppRuleStore.Rules);
        Assert.True(loaded.Auto);
        Assert.Equal(screen, loaded.Screen);

        IccAppRuleStore.Save([loaded with { Screen = new AppRuleScreen(Contrast: 500) }]);
        var dropped = Assert.Single(IccAppRuleStore.Rules);
        Assert.Null(dropped.Screen);
        Assert.True(dropped.Auto);
    }

    [Fact]
    public void Watcher_keeps_the_filter_while_the_game_runs_when_restore_on_leave_is_off()
    {
        SettingsService.Current.FilterRestoreOnFocusLoss = false;
        IccAppRuleStore.Save([new FilterAppRule("cs2.exe", "generated:Vivid", 80, true)]);
        UseForeground(new ForegroundAppInfo("cs2.exe", @"C:\Games\cs2.exe", "Counter-Strike 2", 4242));
        var alive = true;
        ForegroundFilterWatcher.ProcessAliveOverride = _ => alive;
        var counters = UseWatcher();
        using var watcher = new ForegroundFilterWatcher();
        watcher.Poll(); watcher.Poll();
        Assert.Equal(1, counters.Applies());
        // 切到桌面：游戏还开着，滤镜保持不动。
        UseForeground(new ForegroundAppInfo("explorer.exe"));
        watcher.Poll(); watcher.Poll();
        Assert.Equal(0, counters.Restores());
        Assert.Contains("FilterWatcherKeeping", watcher.Status.Detail);
        // 游戏退出：这才还原。
        alive = false;
        watcher.Poll();
        Assert.Equal(1, counters.Restores());
        Assert.Null(watcher.Status.ActivePreset);
    }

    [Fact]
    public void Watcher_restores_when_leaving_the_game_by_default_and_pause_stops_following_until_resumed()
    {
        SettingsService.Current.FilterRestoreOnFocusLoss = true;
        IccAppRuleStore.Save([new FilterAppRule("cs2.exe", "generated:Vivid", 80, true)]);
        UseForeground(new ForegroundAppInfo("cs2.exe", @"C:\Games\cs2.exe", "Counter-Strike 2", 4242));
        var counters = UseWatcher();
        using var watcher = new ForegroundFilterWatcher();
        watcher.Poll(); watcher.Poll();
        Assert.Equal(1, counters.Applies());
        UseForeground(new ForegroundAppInfo("explorer.exe"));
        watcher.Poll(); watcher.Poll();
        Assert.Equal(1, counters.Restores());

        // 停止：立刻还原，并且不再跟随（开关仍然开着）。
        UseForeground(new ForegroundAppInfo("cs2.exe", @"C:\Games\cs2.exe", "Counter-Strike 2", 4242));
        watcher.Poll(); watcher.Poll();
        Assert.Equal(2, counters.Applies());
        watcher.Pause();
        Assert.True(watcher.Status.Paused);
        Assert.Equal(2, counters.Restores());
        watcher.Poll(); watcher.Poll();
        Assert.Equal(2, counters.Applies());
        Assert.Contains("FilterWatcherStopped", watcher.Status.Detail);
        // 重新启动：恢复跟随。
        watcher.Resume(); watcher.Poll();
        Assert.Equal(3, counters.Applies());
        Assert.False(watcher.Status.Paused);
    }

    [Fact]
    public void Watcher_applies_the_auto_template_to_unlisted_games_inside_the_selected_scope()
    {
        FilterSchemeStore.SaveAutoTemplate("generated:Vivid", 66, true, null);
        var settings = SettingsService.Current;
        settings.FilterAutoFallbackEnabled = true;
        settings.FilterAutoScope = "gamedir";
        var counters = UseWatcher();

        // 不在游戏目录里的程序：不套。
        UseForeground(new ForegroundAppInfo("editor.exe", @"D:\Tools\editor\editor.exe", "Editor", 100));
        using (var watcher = new ForegroundFilterWatcher())
        {
            watcher.Poll(); watcher.Poll();
            Assert.Equal(0, counters.Applies());
            Assert.Null(watcher.Status.ActivePreset);
        }

        // Steam 库里的游戏：按模板套上，并标记为自动。
        UseForeground(new ForegroundAppInfo("ship-Win64-Shipping.exe",
            @"F:\SteamLibrary\steamapps\common\Ship\ship-Win64-Shipping.exe", "Ship", 200));
        using (var watcher = new ForegroundFilterWatcher())
        {
            watcher.Poll(); watcher.Poll();
            Assert.Equal(1, counters.Applies());
            Assert.Equal("generated:Vivid", watcher.Status.ActivePreset);
            Assert.True(watcher.Status.ActiveAuto);
        }

        // 关掉兜底：同样前台不再套（也证明不是靠规则命中）。
        settings.FilterAutoFallbackEnabled = false;
        using (var watcher = new ForegroundFilterWatcher())
        {
            watcher.Poll(); watcher.Poll();
            Assert.Equal(1, counters.Applies());
            Assert.Null(watcher.Status.ActivePreset);
        }
    }
}
