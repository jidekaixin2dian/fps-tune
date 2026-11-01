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
