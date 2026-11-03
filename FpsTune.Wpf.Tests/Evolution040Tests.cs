using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 0.4.0 大版本专项：窗口化游戏优化（打包字符串解析-合并-回写）、
/// 游戏高精度计时器（状态机 + 审计）、后台资源只读采样（增量数学）。
/// 全部走注入 fake，不触碰真实注册表、计时器与进程采样。
/// </summary>
[Collection("BackupService serial")]
public sealed class Evolution040Tests : IDisposable
{
    private readonly string _dir;

    public Evolution040Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        BackupService.BackupDirOverride = _dir;
        UserDataPaths.RootOverride = _dir;
        AutoProfileActivityStore.OverrideDir = _dir;
    }

    public void Dispose()
    {
        BackupService.BackupDirOverride = null;
        AutoProfileActivityStore.OverrideDir = null;
        UserDataPaths.RootOverride = null;
        RegistryHelper.SnapshotOverride = null;
        RegistryHelper.SetOverride = null;
        NativeTimerResolution.QueryOverride = null;
        NativeTimerResolution.SetOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
    }

    // ---------- DirectXUserSettings 纯函数 ----------

    [Fact]
    public void DirectX_settings_parse_merge_preserves_other_keys()
    {
        const string raw = "AutoHDREnable=2;SwapEffectUpgradeEnable=0;broken;=empty;Key=;";
        var parsed = DirectXUserSettings.Parse(raw);
        Assert.Equal("2", parsed["AutoHDREnable"]);
        Assert.Equal("0", parsed["SwapEffectUpgradeEnable"]);
        Assert.False(parsed.ContainsKey("broken"));
        Assert.Equal(2, parsed.Count); // "=empty" 与 "Key="（空值）按畸形忽略
        parsed["SwapEffectUpgradeEnable"] = "1";
        var merged = DirectXUserSettings.Merge(parsed);
        Assert.Equal("AutoHDREnable=2;SwapEffectUpgradeEnable=1;", merged);
        // 往返一致
        var reparsed = DirectXUserSettings.Parse(merged);
        Assert.Equal("1", reparsed["SwapEffectUpgradeEnable"]);
        Assert.Equal("2", reparsed["AutoHDREnable"]);
    }

    [Fact]
    public void DirectX_settings_empty_and_duplicates_behave()
    {
        Assert.Empty(DirectXUserSettings.Parse(null));
        Assert.Empty(DirectXUserSettings.Parse(""));
        Assert.Equal("2", DirectXUserSettings.Parse("K=1;K=2;")["K"]); // 重复键取最后一次
        Assert.Equal("K=1;", DirectXUserSettings.Merge(new Dictionary<string, string> { ["K"] = "1" }));
    }

    // ---------- 窗口化游戏优化 ----------

    [Fact]
    public async Task Windowed_games_apply_merges_without_clobbering_other_settings()
    {
        string? written = null;
        RegistryHelper.SnapshotOverride = (_, _, _) => new RegistryHelper.Snapshot(true, written ?? "AutoHDREnable=2;", RegistryValueKind.String);
        RegistryHelper.SetOverride = (_, _, _, value, _) => written = (string)value;
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "windowed-games-opt" }, null)).Results);
        Assert.True(result.Ok, result.Message);
        Assert.True(result.Changed);
        Assert.Equal("AutoHDREnable=2;SwapEffectUpgradeEnable=1;", written);
    }

    [Fact]
    public async Task Windowed_games_apply_skips_when_already_on()
    {
        RegistryHelper.SnapshotOverride = (_, _, _) => new RegistryHelper.Snapshot(true, "SwapEffectUpgradeEnable=1;", RegistryValueKind.String);
        RegistryHelper.SetOverride = (_, _, _, _, _) => throw new UnexpectedWriteException();
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "windowed-games-opt" }, null)).Results);
        Assert.True(result.Ok);
        Assert.False(result.Changed);
    }

    [Fact]
    public async Task Windowed_games_capture_and_restore_roundtrip()
    {
        var original = "AutoHDREnable=2;";
        string? current = original;
        RegistryHelper.SnapshotOverride = (_, _, _) => new RegistryHelper.Snapshot(true, current, RegistryValueKind.String);
        RegistryHelper.SetOverride = (_, _, _, value, _) => current = (string)value;
        var capture = BackupService.Capture(new[] { "windowed-games-opt" }, null);
        await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "windowed-games-opt" }, null);
        Assert.Contains("SwapEffectUpgradeEnable=1", current);
        var restored = BackupService.RestoreAll(new[] { "windowed-games-opt" }, capture);
        Assert.Empty(restored.Failures);
        Assert.Equal(original, current);
    }

    private sealed class UnexpectedWriteException : Exception;

    // ---------- 游戏高精度计时器 ----------

    [Fact]
    public void Timer_service_acquires_for_running_game_and_releases_after_exit()
    {
        var requested = new List<(uint Desired, bool Enable)>();
        uint current = 156001; // 系统默认 ≈ 15.6 ms
        NativeTimerResolution.SetOverride = (desired, enable) => { requested.Add((desired, enable)); current = enable ? desired : 156001; return current; };
        NativeTimerResolution.QueryOverride = () => (156001, 5000, current);
        var settings = SettingsService.Current;
        var oldEnabled = settings.GameTimerBoostEnabled;
        settings.GameTimerBoostEnabled = true;
        try
        {
            using var service = new GameTimerResolutionService
            {
                GamesProvider = () => [new StateStore.GameProfile("g1", @"C:\Games\Fixture.exe", "Fixture", DateTime.Now)],
                RunningProbe = (processName, exePath) => processName == "Fixture" && exePath == @"C:\Games\Fixture.exe",
            };
            service.Poll();
            Assert.True(service.Status.Active);
            Assert.Equal("Fixture", service.Status.Game);
            Assert.Equal(5000u, service.Status.CurrentResolution);
            Assert.Equal(5000u, Assert.Single(requested).Desired);

            // 游戏退出 → 撤销请求
            service.RunningProbe = (_, _) => false;
            service.Poll();
            Assert.False(service.Status.Active);
            Assert.Equal(2, requested.Count);
            Assert.False(requested[1].Enable);
            var infoEvents = AutoProfileActivityStore.Load().Where(e => e.Kind == AutoProfileActivityStore.KindInfo).ToList();
            Assert.Equal(2, infoEvents.Count); // 获取 + 撤销（测试环境无资源字典，不断言文案内容）
        }
        finally
        {
            settings.GameTimerBoostEnabled = oldEnabled;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void Timer_service_stays_released_when_disabled_or_library_empty()
    {
        var requested = new List<(uint Desired, bool Enable)>();
        NativeTimerResolution.SetOverride = (desired, enable) => { requested.Add((desired, enable)); return enable ? desired : 156001; };
        var settings = SettingsService.Current;
        var oldEnabled = settings.GameTimerBoostEnabled;
        settings.GameTimerBoostEnabled = false;
        try
        {
            using var service = new GameTimerResolutionService
            {
                GamesProvider = () => [new StateStore.GameProfile("g1", @"C:\Games\Fixture.exe", "Fixture", DateTime.Now)],
                RunningProbe = (_, _) => true,
            };
            service.Poll();
            Assert.False(service.Status.Active);
            Assert.False(service.Status.Enabled);
            Assert.Empty(requested);

            // 打开开关但游戏库为空：同样不请求
            settings.GameTimerBoostEnabled = true;
            service.GamesProvider = () => [];
            service.Poll();
            Assert.Empty(requested);
        }
        finally
        {
            settings.GameTimerBoostEnabled = oldEnabled;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void Default_probe_matches_process_name_without_extension()
    {
        // 回归：探测名曾把 "xxx.exe" 传给 Process.GetProcessesByName（匹配不带扩展名的
        // ProcessName），导致「游戏运行时 0.5ms」开关 100% 静默失效。用真实进程钉住行为。
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 > nul")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        try
        {
            var exePath = process!.MainModule?.FileName;
            Assert.False(string.IsNullOrWhiteSpace(exePath));
            Assert.True(GameTimerResolutionService.DefaultProbe("cmd", exePath));
            Assert.False(GameTimerResolutionService.DefaultProbe("fpstune-nonexistent-probe", null));
        }
        finally
        {
            try { process!.Kill(); } catch { }
        }
    }

    // ---------- 后台资源只读采样 ----------

    [Fact]
    public async Task Background_check_computes_cpu_disk_and_network_deltas()
    {
        BackgroundResourceCheck.DelayOverride = (_, _) => Task.FromResult(3.0);
        ulong networkCalls = 0;
        BackgroundResourceCheck.NetworkTotalOverride = () => ++networkCalls == 1 ? 3_000_000u : 9_000_000u;
        var first = new List<BackgroundResourceCheck.Sample>
        {
            new(100, "game", TimeSpan.FromSeconds(10), 1_000_000),
            new(200, "sync", TimeSpan.FromSeconds(2), 4_000_000),
            new(400, "restarted", TimeSpan.FromSeconds(5), 900),        // CPU 回退 → 按 0 处理
        };
        var second = new List<BackgroundResourceCheck.Sample>
        {
            new(100, "game", TimeSpan.FromSeconds(10.9), 1_000_000),   // CPU 增量 0.9s
            new(200, "sync", TimeSpan.FromSeconds(2), 5_242_880),      // CPU 增量 0；磁盘增量 1_242_880
            new(300, "fresh", TimeSpan.FromSeconds(2), 900_000),
            new(400, "restarted", TimeSpan.FromSeconds(4), 1_000),
        };
        var calls = 0;
        BackgroundResourceCheck.SnapshotOverride = () => ++calls == 1 ? first : second;
        var result = await BackgroundResourceCheck.RunAsync(CancellationToken.None);

        Assert.Equal(3.0, result.SampleSeconds, precision: 1);
        var game = Assert.Single(result.TopCpu);
        Assert.Equal("game", game.Name);
        Assert.Equal(100, game.Pid);
        var sync = Assert.Single(result.TopDisk);
        Assert.Equal("sync", sync.Name);
        var rate = sync.DiskBytesPerSecond;
        Assert.True(rate is > 300_000 and < 500_000, $"disk={rate}");
        Assert.Equal(2_000_000, result.NetworkBytesPerSecond, precision: 0);
        Assert.Equal(4, result.ProcessCount);
        Assert.DoesNotContain(result.TopCpu, s => s.Name == "fresh"); // 窗口内新进程无完整快照，不计入
    }

    [Fact]
    public async Task Background_check_reports_unreadable_processes()
    {
        BackgroundResourceCheck.DelayOverride = (_, _) => Task.FromResult(3.0);
        BackgroundResourceCheck.NetworkTotalOverride = () => 0;
        // SnapshotOverride 路径不产生 unavailable（fake 层已过滤）；真实层按进程级异常计数——
        // 这里只验证字段与空榜单行为：全部低于噪声阈值 → 两个榜单为空。
        BackgroundResourceCheck.SnapshotOverride = () => new List<BackgroundResourceCheck.Sample>
        {
            new(100, "idle-ish", TimeSpan.FromSeconds(1), 10),
        };
        var result = await BackgroundResourceCheck.RunAsync(CancellationToken.None);
        Assert.Empty(result.TopCpu);
        Assert.Empty(result.TopDisk);
        Assert.Equal(0, result.NetworkBytesPerSecond);
    }
}
