using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 全屏自动切换：判定 / 去抖 / 闸门 / 互斥。
///
/// 全部通过注入 <see cref="IForegroundProbe"/> 与假 gamma API 来跑，**不碰真实窗口与真实显示器**：
/// 判定逻辑是本功能最容易出错也最容易做错的地方（全屏切换瞬间状态会抖好几次），
/// 所以把它做成纯状态机再加测试，而不是靠肉眼在游戏里看。
/// </summary>
[Collection("BackupService serial")]
public sealed class FullscreenDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-fullscreen-" + Guid.NewGuid().ToString("N"));

    private const string DisplayId = "ID-A";
    private static readonly PixelRect Screen1080p = new(0, 0, 1920, 1080);

    public FullscreenDetectorTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        FullscreenPendingStore.DirectoryOverride = Path.Combine(_root, "pending");
        AutoProfileActivityStore.OverrideDir = _root;
        SettingsService.Load();
        FullscreenDetector.DisplayIdentityOverride = _ => DisplayId;
    }

    public void Dispose()
    {
        DisplayTopology.Override = null;
        FullscreenDetector.DisplayIdentityOverride = null;
        TransientGamma.ApiOverride = null;
        FullscreenPendingStore.DirectoryOverride = null;
        AutoProfileActivityStore.OverrideDir = null;
        UserDataPaths.RootOverride = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---------- 假探针 ----------

    private sealed class FakeProbe : IForegroundProbe
    {
        internal nint Window { get; set; } = 11;
        internal string? Process { get; set; } = "cs2.exe";
        internal PixelRect? Bounds { get; set; } = Screen1080p;
        internal PixelRect Monitor { get; set; } = Screen1080p;
        internal string Device { get; set; } = @"\\.\DISPLAY1";
        internal bool MonitorReadable { get; set; } = true;
        internal int? Notification { get; set; } = 5;
        internal bool Cloaked { get; set; }
        internal string? ClassName { get; set; } = "UnrealWindow";
        internal bool CaptionOrBorder { get; set; }

        public nint GetForegroundWindow() => Window;
        public nint GetShellWindow() => 22;
        public nint GetDesktopWindow() => 33;
        public bool TryGetProcessName(nint window, out string processName)
        {
            processName = Process ?? "";
            return Process is not null;
        }
        public bool TryGetWindowBounds(nint window, out PixelRect bounds)
        {
            bounds = Bounds ?? default;
            return Bounds is not null;
        }
        public bool TryGetMonitor(nint window, out PixelRect bounds, out string gdiDeviceName)
        {
            bounds = Monitor;
            gdiDeviceName = Device;
            return MonitorReadable;
        }
        public bool TryGetNotificationState(out int state)
        {
            state = Notification ?? 0;
            return Notification is not null;
        }
        public bool IsCloaked(nint window) => Cloaked;
        public bool HasCaptionOrBorder(nint window) => CaptionOrBorder;
        public string? ClassNameOf(nint window) => ClassName;
    }

    private static ForegroundScreenState Evaluate(FakeProbe probe, out string? identity, out string? process)
        => new FullscreenDetector(probe).Evaluate(out identity, out process);

    // ---------- 判定 ----------

    [Fact]
    public void Borderless_fullscreen_when_window_covers_the_monitor()
    {
        var state = Evaluate(new FakeProbe(), out var identity, out var process);
        Assert.Equal(ForegroundScreenState.FullscreenBorderless, state);
        Assert.Equal(DisplayId, identity);
        Assert.Equal("cs2.exe", process);
    }

    [Fact]
    public void Borderless_fullscreen_tolerates_two_pixel_offset()
    {
        // DPI 缩放/投影会让窗口矩形与显示器矩形差 1-2px。
        var probe = new FakeProbe { Bounds = new PixelRect(2, 0, 1922, 1080) };
        Assert.Equal(ForegroundScreenState.FullscreenBorderless, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Bordered_maximized_window_is_not_fullscreen()
    {
        // 真机实测：Windows 最大化窗口的 DWM 扩展边框被外扩到屏幕边缘，与无边框全屏同形，
        // 只靠矩形会把"窗口化最大化"误判成全屏，必须有样式闸门。
        var probe = new FakeProbe { CaptionOrBorder = true };
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Bordered_window_still_counts_as_exclusive_when_the_shell_reports_d3d_fullscreen()
    {
        // 独占全屏可能仍带样式位，靠 notification == 3 兜住，别被样式闸门连坐。
        var probe = new FakeProbe { CaptionOrBorder = true, Notification = 3 };
        Assert.Equal(ForegroundScreenState.FullscreenExclusive, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Window_far_larger_than_its_monitor_is_not_fullscreen()
    {
        // 非 PMv2 进程会把逻辑像素与物理像素混用：窗口"覆盖"显示器其实是坐标系错位。
        var probe = new FakeProbe { Bounds = new PixelRect(-8, -8, 2568, 1608) };
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Exclusive_fullscreen_falls_back_to_notification_state()    {
        // 独占全屏下窗口矩形可能小于显示器，靠 SHQueryUserNotificationState == 3 兜住。
        var probe = new FakeProbe { Bounds = new PixelRect(0, 0, 1280, 720), Notification = 3 };
        Assert.Equal(ForegroundScreenState.FullscreenExclusive, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Windowed_game_is_not_fullscreen()
    {
        var probe = new FakeProbe { Bounds = new PixelRect(100, 100, 1380, 820), Notification = 5 };
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Busy_notification_state_is_not_treated_as_fullscreen()
    {
        // QUNS_BUSY(2) 在演示与部分 UWP 全屏下也会返回，不能当全屏。
        var probe = new FakeProbe { Bounds = new PixelRect(10, 10, 900, 600), Notification = 2 };
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Shell_and_desktop_windows_are_never_fullscreen()
    {
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(new FakeProbe { Window = 22 }, out _, out _));
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(new FakeProbe { Window = 33 }, out _, out _));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void Desktop_shell_window_classes_are_never_fullscreen(string className)
    {
        var probe = new FakeProbe { ClassName = className };
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Cloaked_window_is_never_fullscreen()
    {
        Assert.Equal(ForegroundScreenState.NotFullscreen, Evaluate(new FakeProbe { Cloaked = true }, out _, out _));
    }

    [Fact]
    public void Unreadable_process_name_is_unknown_not_not_fullscreen()
    {
        // 受保护进程（反作弊）取不到名字时必须是 Unknown：当成「不是全屏」会在切换瞬间误还原。
        Assert.Equal(ForegroundScreenState.Unknown, Evaluate(new FakeProbe { Process = null }, out _, out _));
    }

    [Fact]
    public void Unknown_display_identity_is_unknown()
    {
        FullscreenDetector.DisplayIdentityOverride = _ => null;
        Assert.Equal(ForegroundScreenState.Unknown, Evaluate(new FakeProbe(), out var identity, out _));
        Assert.Null(identity);
    }

    [Fact]
    public void Failed_notification_query_without_covering_bounds_is_unknown()
    {
        var probe = new FakeProbe { Bounds = new PixelRect(0, 0, 800, 600), Notification = null };
        Assert.Equal(ForegroundScreenState.Unknown, Evaluate(probe, out _, out _));
    }

    [Fact]
    public void Monitor_that_cannot_be_read_is_unknown()
    {
        Assert.Equal(ForegroundScreenState.Unknown, Evaluate(new FakeProbe { MonitorReadable = false }, out _, out _));
    }

    [Fact]
    public void Fullscreen_targets_the_monitor_the_window_is_on_not_the_primary()
    {
        // 副屏：窗口矩形与副屏矩形对齐，身份必须解析成副屏。
        FullscreenDetector.DisplayIdentityOverride = device =>
            device.EndsWith("DISPLAY2", StringComparison.OrdinalIgnoreCase) ? "ID-B" : DisplayId;
        var probe = new FakeProbe
        {
            Monitor = new PixelRect(1920, 0, 3840, 1080),
            Bounds = new PixelRect(1920, 0, 3840, 1080),
            Device = @"\\.\DISPLAY2",
        };
        Assert.Equal(ForegroundScreenState.FullscreenBorderless, Evaluate(probe, out var identity, out _));
        Assert.Equal("ID-B", identity);
    }

    // ---------- 去抖 ----------

    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Enter_needs_four_consecutive_fullscreen_samples()
    {
        var debouncer = new FullscreenDebouncer(cooldown: TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
            Assert.False(debouncer.Update(ForegroundScreenState.FullscreenBorderless, T0.AddMilliseconds(i * 500)));
        Assert.True(debouncer.Update(ForegroundScreenState.FullscreenBorderless, T0.AddMilliseconds(1500)));
        Assert.True(debouncer.Committed);
    }

    [Fact]
    public void Exit_needs_only_two_samples_because_desktop_tint_is_annoying()
    {
        var debouncer = new FullscreenDebouncer(cooldown: TimeSpan.Zero);
        for (var i = 0; i < 4; i++) debouncer.Update(ForegroundScreenState.FullscreenBorderless, T0.AddMilliseconds(i * 500));
        Assert.True(debouncer.Committed);

        Assert.False(debouncer.Update(ForegroundScreenState.NotFullscreen, T0.AddSeconds(10)));
        Assert.True(debouncer.Update(ForegroundScreenState.NotFullscreen, T0.AddSeconds(10.5)));
        Assert.False(debouncer.Committed);
    }

    [Fact]
    public void Unknown_samples_neither_enter_nor_leave_the_window()
    {
        var debouncer = new FullscreenDebouncer(cooldown: TimeSpan.Zero);
        for (var i = 0; i < 4; i++) debouncer.Update(ForegroundScreenState.FullscreenBorderless, T0.AddMilliseconds(i * 500));
        Assert.True(debouncer.Committed);

        for (var i = 0; i < 8; i++)
            Assert.False(debouncer.Update(ForegroundScreenState.Unknown, T0.AddSeconds(5 + i)));
        Assert.True(debouncer.Committed);
        Assert.Equal(4, debouncer.SampleCount);
    }

    [Fact]
    public void Cooldown_blocks_an_immediate_flip()
    {
        var debouncer = new FullscreenDebouncer(cooldown: TimeSpan.FromSeconds(2));
        for (var i = 0; i < 4; i++) debouncer.Update(ForegroundScreenState.FullscreenBorderless, T0.AddMilliseconds(i * 500));
        Assert.True(debouncer.Committed);

        // 刚进入就立刻退出（Alt-Tab 连按）：冷却期内不许刷写。
        Assert.False(debouncer.Update(ForegroundScreenState.NotFullscreen, T0.AddMilliseconds(1600)));
        Assert.False(debouncer.Update(ForegroundScreenState.NotFullscreen, T0.AddMilliseconds(1700)));
        Assert.True(debouncer.Committed);

        // 冷却过后才允许还原。
        Assert.True(debouncer.Update(ForegroundScreenState.NotFullscreen, T0.AddSeconds(30)));
        Assert.False(debouncer.Committed);
    }

    [Fact]
    public void First_stable_windowed_state_decides_without_acting()
    {
        var debouncer = new FullscreenDebouncer(cooldown: TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
            Assert.False(debouncer.Update(ForegroundScreenState.NotFullscreen, T0.AddMilliseconds(i * 500)));
        Assert.True(debouncer.HasDecision);
        Assert.False(debouncer.Committed);
    }

    [Fact]
    public void Reset_clears_the_decision_after_a_settings_change()
    {
        var debouncer = new FullscreenDebouncer(cooldown: TimeSpan.Zero);
        for (var i = 0; i < 4; i++) debouncer.Update(ForegroundScreenState.FullscreenBorderless, T0.AddMilliseconds(i * 500));
        Assert.True(debouncer.Committed);

        debouncer.Reset();
        Assert.False(debouncer.Committed);
        Assert.False(debouncer.HasDecision);
        Assert.Equal(0, debouncer.SampleCount);
    }

    // ---------- 闸门与互斥 ----------

    [Fact]
    public void Only_for_games_filter_ignores_fullscreen_apps_that_are_not_known_games()
    {
        EnableFullscreen(onlyForGames: true);
        var watcher = new FullscreenPresetWatcher(
            new FakeProbe { Process = "notepad.exe" }, new FullscreenDebouncer(cooldown: TimeSpan.Zero));

        for (var i = 0; i < 4; i++) watcher.Poll();

        Assert.Null(watcher.Status.ActivePreset);
    }

    [Fact]
    public void Disabled_switch_never_engages()
    {
        EnableFullscreen(onlyForGames: false, enabled: false);
        var watcher = new FullscreenPresetWatcher(
            new FakeProbe(), new FullscreenDebouncer(cooldown: TimeSpan.Zero));

        for (var i = 0; i < 6; i++) watcher.Poll();

        Assert.Null(watcher.Status.ActivePreset);
    }

    [Fact]
    public void Missing_preset_never_engages()
    {
        EnableFullscreen(onlyForGames: false, preset: "");
        var watcher = new FullscreenPresetWatcher(
            new FakeProbe(), new FullscreenDebouncer(cooldown: TimeSpan.Zero));

        for (var i = 0; i < 6; i++) watcher.Poll();

        Assert.Null(watcher.Status.ActivePreset);
    }

    [Fact]
    public void Fullscreen_watcher_takes_over_from_the_foreground_rule_watcher()
    {
        // 互斥：两个都开时只生效全屏方案，进程规则暂停。
        EnableFullscreen(onlyForGames: false);
        var settings = SettingsService.Current;
        settings.FilterWatcherEnabled = true;
        SettingsService.Save(settings);

        Assert.True(FullscreenPresetWatcher.SuppressesForegroundRules);

        settings.FilterWatcherEnabled = false;
        settings.FullscreenPresetEnabled = false;
        SettingsService.Save(settings);
        Assert.False(FullscreenPresetWatcher.SuppressesForegroundRules);
    }

    // ---------- 端到端（假显示 + 假 gamma） ----------

    private sealed class FakeGamma : IScreenGammaApi
    {
        internal ushort[] Ramp = LinearRamp();
        internal int Writes;

        public bool IsHdr(IccDisplayInfo display) => false;
        public ushort[] Read(IccDisplayInfo display) => (ushort[])Ramp.Clone();
        public void Write(IccDisplayInfo display, ushort[] ramp)
        {
            Ramp = (ushort[])ramp.Clone();
            Writes++;
        }
    }

    private static ushort[] LinearRamp() => Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();

    [Fact]
    public void Fullscreen_engage_applies_on_top_of_the_current_ramp_and_restores_the_snapshot()
    {
        EnableFullscreen(onlyForGames: true, preset: nameof(IccFilterPreset.Vivid), channel: "gamma");
        DisplayTopology.Override = () => DisplayTopology.MarkIndependent([
            new IccDisplayInfo(@"\\.\DISPLAY1", 123, 0, 0, "0001", true, DisplayId, "Screen A", true),
        ]);

        // 用户已经在「画面与色彩」里调过：当前 ramp 不是线性。
        var gamma = new FakeGamma { Ramp = ScreenColorService.BuildRamp(new ScreenColorSettings(Contrast: 104), LinearRamp()) };
        var userRamp = (ushort[])gamma.Ramp.Clone();
        TransientGamma.ApiOverride = gamma;

        var probe = new FakeProbe();
        var watcher = new FullscreenPresetWatcher(probe, new FullscreenDebouncer(cooldown: TimeSpan.Zero));

        for (var i = 0; i < 4; i++) watcher.Poll();
        Assert.NotNull(watcher.Status.ActivePreset);
        Assert.NotEqual(userRamp, gamma.Ramp);
        Assert.True(File.Exists(FullscreenPendingStore.FilePath));

        // 退出全屏 → 写回进入时的快照，而不是硬件默认曲线。
        probe.Bounds = new PixelRect(100, 100, 900, 600);
        probe.Notification = 5;
        for (var i = 0; i < 2; i++) watcher.Poll();

        Assert.Null(watcher.Status.ActivePreset);
        Assert.Equal(userRamp, gamma.Ramp);
        Assert.False(File.Exists(FullscreenPendingStore.FilePath));
    }

    [Fact]
    public void Pending_receipt_from_a_crashed_run_is_restored_on_next_start()
    {
        DisplayTopology.Override = () => DisplayTopology.MarkIndependent([
            new IccDisplayInfo(@"\\.\DISPLAY1", 123, 0, 0, "0001", true, DisplayId, "Screen A", true),
        ]);
        var gamma = new FakeGamma { Ramp = LinearRamp() };
        TransientGamma.ApiOverride = gamma;

        var original = (ushort[])gamma.Ramp.Clone();
        var expected = ScreenColorService.BuildRamp(ScreenColorSettings.ForPreset(IccFilterPreset.Vivid), original);

        // 模拟「应用后进程被杀」：屏上是 expected，磁盘上留着凭证。
        gamma.Ramp = (ushort[])expected.Clone();
        FullscreenPendingStore.Save(new FullscreenPending(
            FullscreenPendingStore.CurrentVersion, DisplayId, FullscreenPresetWatcher.ChannelGamma,
            nameof(IccFilterPreset.Vivid), DateTime.Now, original, expected));

        Assert.True(FullscreenPresetWatcher.RestoreOnStartup());
        Assert.Equal(original, gamma.Ramp);
        Assert.False(File.Exists(FullscreenPendingStore.FilePath));
    }

    [Fact]
    public void Pending_receipt_is_cleared_when_the_screen_already_holds_the_original_curve()
    {
        DisplayTopology.Override = () => DisplayTopology.MarkIndependent([
            new IccDisplayInfo(@"\\.\DISPLAY1", 123, 0, 0, "0001", true, DisplayId, "Screen A", true),
        ]);
        var gamma = new FakeGamma { Ramp = LinearRamp() };
        TransientGamma.ApiOverride = gamma;

        var original = (ushort[])gamma.Ramp.Clone();
        var expected = ScreenColorService.BuildRamp(ScreenColorSettings.ForPreset(IccFilterPreset.Vivid), original);
        FullscreenPendingStore.Save(new FullscreenPending(
            FullscreenPendingStore.CurrentVersion, DisplayId, FullscreenPresetWatcher.ChannelGamma,
            nameof(IccFilterPreset.Vivid), DateTime.Now, original, expected));

        // 屏上已经是原始曲线（例如用户自己还原过）：不应报“被外部接管”，凭证也不该留。
        Assert.True(FullscreenPresetWatcher.RestoreOnStartup());
        Assert.Equal(original, gamma.Ramp);
        Assert.False(File.Exists(FullscreenPendingStore.FilePath));
    }

    [Fact]
    public void Startup_restore_refuses_to_overwrite_when_the_user_changed_colors_meanwhile()
    {
        DisplayTopology.Override = () => DisplayTopology.MarkIndependent([
            new IccDisplayInfo(@"\\.\DISPLAY1", 123, 0, 0, "0001", true, DisplayId, "Screen A", true),
        ]);
        var gamma = new FakeGamma { Ramp = LinearRamp() };
        TransientGamma.ApiOverride = gamma;

        var original = (ushort[])gamma.Ramp.Clone();
        var expected = ScreenColorService.BuildRamp(ScreenColorSettings.ForPreset(IccFilterPreset.Vivid), original);
        FullscreenPendingStore.Save(new FullscreenPending(
            FullscreenPendingStore.CurrentVersion, DisplayId, FullscreenPresetWatcher.ChannelGamma,
            nameof(IccFilterPreset.Vivid), DateTime.Now, original, expected));

        // 屏上已经不是我们写的那一版：拒绝覆盖，并保留凭证。
        var foreign = ScreenColorService.BuildRamp(new ScreenColorSettings(Gamma: 1.2), original);
        gamma.Ramp = (ushort[])foreign.Clone();

        Assert.False(FullscreenPresetWatcher.RestoreOnStartup());
        Assert.Equal(foreign, gamma.Ramp);
        Assert.True(File.Exists(FullscreenPendingStore.FilePath));
    }

    private static void EnableFullscreen(bool onlyForGames, bool enabled = true,
        string preset = nameof(IccFilterPreset.Vivid), string channel = "gamma")
    {
        var settings = SettingsService.Current;
        settings.FullscreenPresetEnabled = enabled;
        settings.FullscreenPresetOnlyForGames = onlyForGames;
        settings.FullscreenPresetName = preset;
        settings.FullscreenPresetChannel = channel;
        SettingsService.Save(settings);
    }
}
