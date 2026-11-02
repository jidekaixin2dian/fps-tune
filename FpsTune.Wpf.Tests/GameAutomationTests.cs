using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 0.4.0 续轮：游戏自动化套件专项。
/// 启动/退出边沿状态机、自动开局准备的执行与跳过、自动最高刷新率的切换与还原、
/// 本局小结通知构造。全部注入 fake，不触碰真实显示器、准备流程与录制。
/// </summary>
[Collection("BackupService serial")]
public sealed class GameAutomationTests : IDisposable
{
    private readonly string _dir;

    public GameAutomationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AutoProfileActivityStore.OverrideDir = _dir;
        UserDataPaths.RootOverride = _dir;
    }

    public void Dispose()
    {
        AutoProfileActivityStore.OverrideDir = null;
        UserDataPaths.RootOverride = null;
        TrayService.NotifyOverride = null;
        NativeProcessScheduling.ReadOverride = null;
        NativeProcessScheduling.WriteAffinityOverride = null;
        NativeProcessScheduling.WritePriorityOverride = null;
    }

    private static StateStore.GameProfile Game(string name = "Fixture", string exe = @"C:\Games\Fixture.exe")
        => new("g1", exe, name, DateTime.Now);

    private static IccDisplayInfo Display(string device = @"\\.\DISPLAY1")
        => new(device, 1u, 0, 0u, "sub", true, FriendlyName: "主显示器");

    [Fact]
    public void Automation_runs_preparation_on_launch_edge_once_per_launch()
    {
        var settings = SettingsService.Current;
        var oldPrep = settings.AutoPreparationEnabled;
        var oldRefresh = settings.AutoRefreshBoostEnabled;
        settings.AutoPreparationEnabled = true;
        settings.AutoRefreshBoostEnabled = false;
        try
        {
            var runs = new List<string>();
            var game = Game();
            var running = false;
            using var service = new GameSessionAutomationService
            {
                GamesProvider = () => [game],
                Probe = _ => new GameProcessObservation(running ? new GameProcessRun(100, 1) : null),
                PreparationResolver = _ => new GamePreparationOptions { Configured = true, CleanMemory = false, ApplyIcc = true, IccPreset = IccFilterPreset.Warm, DisplayIdentity = "DISPLAY1" },
                PreparationRunner = (options, exe) => { runs.Add(exe); return Task.FromResult<IReadOnlyList<PreparationStep>>([new("s", true, "ok")]); },
            };
            service.Poll(); // 基线：应用启动时游戏已在运行不算启动
            Assert.Empty(runs);
            running = true; service.Poll(); // 启动边沿 → 执行一次
            service.Poll(); // 已在运行：不是新边沿
            running = false; service.Poll();
            running = true; service.Poll(); // 再次启动 → 再执行
            Assert.Equal(2, runs.Count);
            Assert.All(runs, exe => Assert.Equal(game.ExePath, exe));
            var details = AutoProfileActivityStore.Load().Where(e => e.Kind == AutoProfileActivityStore.KindInfo).ToList();
            Assert.True(details.Count >= 2);
        }
        finally
        {
            settings.AutoPreparationEnabled = oldPrep;
            settings.AutoRefreshBoostEnabled = oldRefresh;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void Automation_skips_preparation_without_a_configured_plan()
    {
        var settings = SettingsService.Current;
        var oldPrep = settings.AutoPreparationEnabled;
        var oldRefresh = settings.AutoRefreshBoostEnabled;
        settings.AutoPreparationEnabled = true;
        settings.AutoRefreshBoostEnabled = false;
        try
        {
            var runs = 0;
            var game = Game();
            var running = false;
            using var service = new GameSessionAutomationService
            {
                GamesProvider = () => [game],
                Probe = _ => new GameProcessObservation(running ? new GameProcessRun(100, 1) : null),
                PreparationResolver = _ => new GamePreparationOptions { Configured = false },
                PreparationRunner = (options, exe) => { runs++; return Task.FromResult<IReadOnlyList<PreparationStep>>([]); },
            };
            service.Poll();
            running = true; service.Poll();
            Assert.Equal(0, runs); // 未配置方案：跳过并记录
        }
        finally
        {
            settings.AutoPreparationEnabled = oldPrep;
            settings.AutoRefreshBoostEnabled = oldRefresh;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void Automation_engages_max_refresh_on_start_and_restores_after_all_games_exit()
    {
        var settings = SettingsService.Current;
        var oldPrep = settings.AutoPreparationEnabled;
        var oldRefresh = settings.AutoRefreshBoostEnabled;
        settings.AutoPreparationEnabled = false;
        settings.AutoRefreshBoostEnabled = true;
        var display = Display();
        var fake = new FakeModeApi(currentHz: 60);
        try
        {
            var game = Game();
            var running = false;
            using var service = new GameSessionAutomationService
            {
                GamesProvider = () => [game],
                ModeApi = fake,
                DisplaysProvider = () => [display],
                Probe = _ => new GameProcessObservation(running ? new GameProcessRun(100, 1) : null),
            };
            service.Poll(); // 基线
            running = true; service.Poll(); // 启动 → 60 → 165
            Assert.Equal(165, fake.CurrentHz);
            Assert.Equal([(display.GdiDeviceName, 165)], fake.Applied);
            running = false; service.Poll(); // 全部退出 → 还原 60
            Assert.Equal(60, fake.CurrentHz);
            Assert.Equal(2, fake.Applied.Count);
            Assert.Equal((display.GdiDeviceName, 60), fake.Applied[1]);
        }
        finally
        {
            settings.AutoPreparationEnabled = oldPrep;
            settings.AutoRefreshBoostEnabled = oldRefresh;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void Automation_disabling_switch_mid_game_restores_immediately()
    {
        var settings = SettingsService.Current;
        var oldPrep = settings.AutoPreparationEnabled;
        var oldRefresh = settings.AutoRefreshBoostEnabled;
        settings.AutoPreparationEnabled = false;
        settings.AutoRefreshBoostEnabled = true;
        var display = Display();
        var fake = new FakeModeApi(currentHz: 60);
        try
        {
            var game = Game();
            var running = false;
            using var service = new GameSessionAutomationService
            {
                GamesProvider = () => [game],
                ModeApi = fake,
                DisplaysProvider = () => [display],
                Probe = _ => new GameProcessObservation(running ? new GameProcessRun(100, 1) : null),
            };
            service.Poll();
            running = true; service.Poll();
            Assert.Equal(165, fake.CurrentHz);
            // 游戏运行中关闭开关：立即还原，不留挂起状态
            settings.AutoRefreshBoostEnabled = false;
            service.ApplySettings();
            Assert.Equal(60, fake.CurrentHz);
            Assert.Equal(2, fake.Applied.Count);
        }
        finally
        {
            settings.AutoPreparationEnabled = oldPrep;
            settings.AutoRefreshBoostEnabled = oldRefresh;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void PickTarget_prefers_highest_hz_same_resolution_and_skips_when_already_max()
    {
        var current = new DisplayMode(2560, 1440, 60);
        DisplayMode[] modes =
        [
            new(2560, 1440, 144), new(2560, 1440, 165), new(2560, 1440, 60),
            new(1920, 1080, 240), // 不同分辨率不参与
        ];
        Assert.Equal(165, GameSessionAutomationService.PickTarget(current, modes)?.Hz);
        Assert.Null(GameSessionAutomationService.PickTarget(new DisplayMode(2560, 1440, 165), modes));
    }

    [Fact]
    public void Session_summary_notifies_only_with_reliable_fps_data()
    {
        var notifications = new List<(string Title, string Message)>();
        TrayService.NotifyOverride = (t, m) => notifications.Add((t, m));
        var withFps = new PerformanceSession("s1", "对局", DateTime.Now.AddMinutes(-20), DateTime.Now, 1, 1,
            [], Fps: new SessionFpsStats(1000, 142.4, 98.1, 12.3, 3));
        var summary = AutoRecordingService.BuildSummary(withFps);
        Assert.NotNull(summary);
        TrayService.NotifyComplete(summary!.Value.Title, summary.Value.Message);
        Assert.Single(notifications);

        var withoutFps = withFps with { Fps = null };
        Assert.Null(AutoRecordingService.BuildSummary(withoutFps));
        var emptyFps = withFps with { Fps = new SessionFpsStats(0, 0, 0, 0, 0) };
        Assert.Null(AutoRecordingService.BuildSummary(emptyFps));
        Assert.Single(notifications); // 无数据不通知
    }

    [Fact]
    public void Split_masks_hybrid_splits_and_homogeneous_does_not()
    {
        var (game, bg) = CpuTopology.SplitMasks([0, 0, 0, 0, 1, 1]);
        Assert.Equal(0b001111u, game);
        Assert.Equal(0b110000u, bg);
        var (sameGame, sameBg) = CpuTopology.SplitMasks([0, 0, 0, 0]);
        Assert.Equal(0b1111u, sameGame);
        Assert.Equal(0u, sameBg); // 同构不隔离亲和性
    }

    [Fact]
    public async Task Affinity_isolation_throttles_selected_apps_and_restores_on_exit()
    {
        var settings = SettingsService.Current;
        var oldAffinity = settings.AutoAffinityIsolateEnabled;
        var oldApps = settings.MemoryBackgroundApps;
        settings.AutoAffinityIsolateEnabled = true;
        settings.AutoPreparationEnabled = false;
        settings.AutoRefreshBoostEnabled = false;
        settings.MemoryBackgroundApps = [@"C:\Apps\Fan.exe"];
        var state = new Dictionary<int, (nuint Affinity, uint Priority)>
        {
            [501] = (0b111111u, 0x20), [502] = (0b111111u, 0x20),
        };
        NativeProcessScheduling.ReadOverride = pid => state.TryGetValue(pid, out var v) ? v : null;
        NativeProcessScheduling.WriteAffinityOverride = (pid, mask) => { if (state.ContainsKey(pid)) { state[pid] = (mask, state[pid].Priority); return true; } return false; };
        NativeProcessScheduling.WritePriorityOverride = (pid, prio) => { if (state.ContainsKey(pid)) { state[pid] = (state[pid].Affinity, prio); return true; } return false; };
        var running = false;
        var pids = new[] { 501, 502 };
        try
        {
            var game = Game();
            using var service = new GameSessionAutomationService
            {
                GamesProvider = () => [game],
                Probe = _ => new GameProcessObservation(running ? new GameProcessRun(100, 1) : null),
                EfficiencyClassesProvider = () => [0, 0, 0, 0, 1, 1],
                PidsByPathOverride = _ => pids,
            };
            service.Poll(); // 基线
            running = true; service.Poll(); // 启动 → 隔离到 E 核（bits 4-5）+ BelowNormal
            Assert.Equal(0b110000u, state[501].Affinity);
            Assert.Equal(0b110000u, state[502].Affinity);
            Assert.Equal(NativeProcessScheduling.PriorityBelowNormal, state[501].Priority);
            pids = [501, 502, 503]; // 游戏运行中新进程 → 下轮轮询同样被隔离
            state[503] = (0b111111u, 0x20);
            service.Poll();
            Assert.Equal(0b110000u, state[503].Affinity);
            running = false; service.Poll(); // 全部退出 → 还原
            Assert.Equal(0b111111u, state[501].Affinity);
            Assert.Equal(0x20u, state[501].Priority);
            Assert.Equal(0b111111u, state[503].Affinity);
        }
        finally
        {
            settings.AutoAffinityIsolateEnabled = oldAffinity;
            settings.MemoryBackgroundApps = oldApps;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public async Task Affinity_isolation_restores_immediately_when_toggled_off_mid_game()
    {
        var settings = SettingsService.Current;
        var oldAffinity = settings.AutoAffinityIsolateEnabled;
        var oldApps = settings.MemoryBackgroundApps;
        settings.AutoAffinityIsolateEnabled = true;
        settings.AutoPreparationEnabled = false;
        settings.AutoRefreshBoostEnabled = false;
        settings.MemoryBackgroundApps = [@"C:\Apps\Fan.exe"];
        var state = new Dictionary<int, (nuint Affinity, uint Priority)> { [501] = (0b111111u, 0x20) };
        NativeProcessScheduling.ReadOverride = pid => state.TryGetValue(pid, out var v) ? v : null;
        NativeProcessScheduling.WriteAffinityOverride = (pid, mask) => { if (state.ContainsKey(pid)) { state[pid] = (mask, state[pid].Priority); return true; } return false; };
        NativeProcessScheduling.WritePriorityOverride = (pid, prio) => { if (state.ContainsKey(pid)) { state[pid] = (state[pid].Affinity, prio); return true; } return false; };
        var running = true;
        try
        {
            var game = Game();
            using var service = new GameSessionAutomationService
            {
                GamesProvider = () => [game],
                Probe = _ => new GameProcessObservation(running ? new GameProcessRun(100, 1) : null),
                EfficiencyClassesProvider = () => [0, 0, 0, 0, 1, 1],
                PidsByPathOverride = _ => [501],
            };
            service.Poll(); // 基线
            service.Poll(); // 启动边沿
            Assert.Equal(0b110000u, state[501].Affinity);
            settings.AutoAffinityIsolateEnabled = false; // 运行中关闭：立即还原
            service.ApplySettings();
            Assert.Equal(0b111111u, state[501].Affinity);
            Assert.Equal(0x20u, state[501].Priority);
        }
        finally
        {
            settings.AutoAffinityIsolateEnabled = oldAffinity;
            settings.MemoryBackgroundApps = oldApps;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public async Task Filter_library_rule_store_and_watcher_follow_foreground()
    {
        // 库：内置资产可读、元数据带说明；生成式预设走 generated: 前缀，其余为 .icc 资产
        var presets = GameFilterLibrary.Presets;
        Assert.True(presets.Count > 30, $"presets={presets.Count}");
        Assert.All(presets, p =>
            Assert.True(p.FileName.StartsWith("generated:", StringComparison.Ordinal)
                || p.FileName.EndsWith(".icc", StringComparison.OrdinalIgnoreCase),
                $"unexpected FileName: {p.FileName}"));
        var bytes = GameFilterLibrary.ReadPreset(presets[0].FileName);
        Assert.Equal("acsp", System.Text.Encoding.ASCII.GetString(bytes, 36, 4)); // ICC 签名在 36 偏移
        // 规则表：保存/匹配/上限
        IccAppRuleStore.Save([new FilterAppRule("cs2", presets[0].FileName), new FilterAppRule("cs2", presets[1].FileName)]);
        Assert.Equal(presets[0].FileName, Assert.Single(IccAppRuleStore.Rules).PresetFileName);
        Assert.NotNull(IccAppRuleStore.Match("CS2.EXE"));
        Assert.Null(IccAppRuleStore.Match("other.exe"));
        // 守护：连续两次确认后应用；离开同样需确认，避免切窗抖动。
        var applied = new List<string?>(); var restores = 0;
        ForegroundFilterWatcher.ApplyOverride = _ => { applied.Add("apply"); return true; };
        ForegroundFilterWatcher.RestoreOverride = () => { restores++; return true; };
        ForegroundFilterWatcher.ForegroundProcessOverride = () => new("cs2");
        ForegroundFilterWatcher.DisplayOverride = () => "DISPLAY1";
        var settings = SettingsService.Current;
        var oldWatcher = settings.FilterWatcherEnabled;
        settings.FilterWatcherEnabled = true;
        try
        {
            using var watcher = new ForegroundFilterWatcher();
            watcher.Poll(); watcher.Poll();
            Assert.True(watcher.Status.ActivePreset is not null);
            ForegroundFilterWatcher.ForegroundProcessOverride = () => new("explorer");
            watcher.Poll(); watcher.Poll();
            Assert.Null(watcher.Status.ActivePreset);
            Assert.Equal(1, restores);
        }
        finally
        {
            settings.FilterWatcherEnabled = oldWatcher;
            SettingsService.Save(settings);
            ForegroundFilterWatcher.ApplyOverride = null;
            ForegroundFilterWatcher.RestoreOverride = null;
            ForegroundFilterWatcher.ForegroundProcessOverride = null;
            ForegroundFilterWatcher.DisplayOverride = null;
        }
    }

    [Fact]
    public async Task Delta_frame_fix_renames_pso_and_saved_as_reversible_backups()
    {
        var root = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Saved", "PSOCache"));
        Directory.CreateDirectory(Path.Combine(root, "Saved", "Settings"));
        File.WriteAllText(Path.Combine(root, "Saved", "Settings", "user.cfg"), "graphics");
        File.WriteAllText(Path.Combine(root, "DeltaForceClient-Win64-Shipping.exe"), "stub");
        var gameExe = Path.Combine(root, "DeltaForceClient-Win64-Shipping.exe");
        var fakeDx = Path.Combine(root, "DxCache");
        Directory.CreateDirectory(fakeDx);
        File.WriteAllText(Path.Combine(fakeDx, "shader.bin"), "stub");
        DeltaFrameFixService.DxCacheDirOverride = fakeDx;
        try
        {
            var scan = DeltaFrameFixService.Scan(gameExe);
            Assert.True(scan.GameFound);
            Assert.Equal(root, scan.GameDir);
            var pso = DeltaFrameFixService.FixPsoCache(gameExe);
            Assert.True(pso.Succeeded);
            Assert.False(Directory.Exists(Path.Combine(root, "Saved", "PSOCache")));
            var renamed = Directory.GetDirectories(Path.Combine(root, "Saved"), "PSOCache.bak-*");
            Assert.Single(renamed);
            // 7.31：Saved 改名 + DXCache（无目录则跳过）
            var steps = DeltaFrameFixService.FixLegacy(gameExe);
            Assert.All(steps, s => Assert.True(s.Succeeded, s.Detail));
            Assert.False(Directory.Exists(Path.Combine(root, "Saved")));
            Assert.False(Directory.Exists(fakeDx)); // DXCache 已改名
            var savedBak = Directory.GetDirectories(root, "Saved.bak-*");
            Assert.Single(savedBak);
            // 可逆：改回原名即恢复
            Directory.Move(savedBak[0], Path.Combine(root, "Saved"));
            Assert.True(File.Exists(Path.Combine(root, "Saved", "Settings", "user.cfg")));
        }
        finally
        {
            DeltaFrameFixService.DxCacheDirOverride = null;
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败可忽略 */ }
        }
        await Task.CompletedTask;
    }

    [Fact]
    public void Crosshair_config_normalizes_and_adaptive_hide()
    {
        Assert.Equal("cross", CrosshairConfig.NormalizeStyle("CROSS"));
        Assert.Equal("cross", CrosshairConfig.NormalizeStyle("bogus"));
        Assert.Equal("dot", CrosshairConfig.NormalizeStyle("dot"));
        Assert.Equal("#00FF66", CrosshairConfig.NormalizeColor("00ff66"));
        Assert.Equal("#00FF66", CrosshairConfig.NormalizeColor("nope"));
        Assert.Equal("#1A2B3C", CrosshairConfig.NormalizeColor("#1a2b3c"));

        var config = new CrosshairConfig(true, "cross", "#00FF66", 12, 4, 2, 90, AdaptiveHide: true);
        Assert.True(config.ShouldHide(rightButtonHeld: true, selfForeground: false));  // AW 据枪隐藏
        Assert.False(config.ShouldHide(rightButtonHeld: false, selfForeground: false));
        Assert.True(config.ShouldHide(rightButtonHeld: false, selfForeground: true)); // 自家前台隐藏
        var nonAdaptive = config with { AdaptiveHide = false };
        Assert.False(nonAdaptive.ShouldHide(rightButtonHeld: true, selfForeground: false));
    }

    [Fact]
    public async Task Crosshair_input_state_reads_right_button_with_injection()
    {
        NativeInputState.RightButtonOverride = () => true;
        Assert.True(NativeInputState.RightButtonHeld());
        NativeInputState.RightButtonOverride = () => false;
        Assert.False(NativeInputState.RightButtonHeld());
        NativeInputState.RightButtonOverride = null;
        // 真实 API 冒烟：不抛异常即可（值取决于物理按键状态）
        _ = NativeInputState.RightButtonHeld();
        await Task.CompletedTask;
    }

    private sealed class FakeModeApi(int currentHz) : IDisplayModeApi
    {
        public int CurrentHz { get; set; } = currentHz;
        public List<(string Device, int Hz)> Applied { get; } = new();

        public DisplayMode Read(IccDisplayInfo display) => new(2560, 1440, CurrentHz);
        public IReadOnlyList<DisplayMode> Modes(IccDisplayInfo display) =>
        [
            new(2560, 1440, 60), new(2560, 1440, 120), new(2560, 1440, 165),
        ];
        public void Test(IccDisplayInfo display, DisplayMode mode) => Apply(display, mode);
        public void Apply(IccDisplayInfo display, DisplayMode mode)
        {
            Applied.Add((display.GdiDeviceName, mode.Hz));
            CurrentHz = mode.Hz;
        }
    }
}
