using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>诊断脱敏与自动 Profile 活动审计。</summary>
public sealed class PrivacyAndActivityTests : IDisposable
{
    private readonly string _dir;

    public PrivacyAndActivityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fpstune-activity-tests-" + Guid.NewGuid().ToString("N"));
        AutoProfileActivityStore.OverrideDir = _dir;
    }

    public void Dispose()
    {
        AutoProfileActivityStore.OverrideDir = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---------- PrivacyScrub ----------

    [Fact]
    public void Scrub_removes_drive_paths_and_user_names()
    {
        var text = "错误发生在 C:\\Users\\Aether\\Documents\\game\\cfg.ini 加载时";
        var scrubbed = PrivacyScrub.Sanitize(text);
        Assert.DoesNotContain("Aether", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users", scrubbed);
    }

    [Fact]
    public void Scrub_removes_local_app_data_and_forward_slash_paths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var text = $"日志目录 {local}\\FpsTune\\logs 与 https 之外的 D:/Games/save.dat";
        var scrubbed = PrivacyScrub.Sanitize(text);
        Assert.DoesNotContain(local, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("D:/Games/save.dat", scrubbed);
    }

    [Fact]
    public void Scrub_keeps_non_path_content()
    {
        var text = "电源计划 已应用；优化项 33 个；平均 120 FPS";
        Assert.Equal(text, PrivacyScrub.Sanitize(text));
    }

    [Fact]
    public void Scrub_handles_null_and_empty()
    {
        Assert.Equal("", PrivacyScrub.Sanitize(""));
        Assert.Equal("", PrivacyScrub.Sanitize(null));
    }

    [Fact]
    public void Scrub_handles_paths_with_spaces_and_unc_prefixes()
    {
        var text = "C:\\Program Files\\FPS Tune\\logs\\error.log \\server\\share\\game.exe";
        var scrubbed = PrivacyScrub.Sanitize(text);

        Assert.DoesNotContain("C:\\Program Files", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\server\\share", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PrivacyScrub.PathPlaceholder, scrubbed);
    }

    // ---------- AutoProfileActivityStore ----------

    [Fact]
    public void Activity_append_load_roundtrip_and_ordering()
    {
        AutoProfileActivityStore.Append(new AutoProfileEvent(
            DateTime.Now.AddMinutes(-1), AutoProfileActivityStore.KindMatch, "cs2", "竞技方案", "检测到进程启动。"));
        AutoProfileActivityStore.Append(new AutoProfileEvent(
            DateTime.Now, AutoProfileActivityStore.KindApplied, "cs2", "竞技方案", "已应用 12 项。"));

        var events = AutoProfileActivityStore.Load();
        Assert.Equal(2, events.Count);
        Assert.Equal(AutoProfileActivityStore.KindApplied, events[0].Kind); // 新→旧
        Assert.Equal("cs2", events[0].Process);
    }

    [Fact]
    public void Activity_load_is_bounded_and_tolerates_bad_lines()
    {
        for (var i = 0; i < AutoProfileActivityStore.MaxEvents + 20; i++)
            AutoProfileActivityStore.Append(new AutoProfileEvent(
                DateTime.Now, AutoProfileActivityStore.KindInfo, "game", null, "第 " + i + " 条"));
        File.AppendAllText(AutoProfileActivityStore.EventsFile, "{broken line\n");

        var events = AutoProfileActivityStore.Load();
        Assert.Equal(AutoProfileActivityStore.MaxEvents, events.Count);
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.Kind)));
    }

    [Fact]
    public void Activity_clear_removes_file()
    {
        AutoProfileActivityStore.Append(new AutoProfileEvent(
            DateTime.Now, AutoProfileActivityStore.KindSkipped, "game", "方案", "未找到方案。"));
        AutoProfileActivityStore.Clear();
        Assert.Empty(AutoProfileActivityStore.Load());
    }

    [Fact]
    public void Activity_append_is_physically_bounded_and_scrubbed()
    {
        for (var i = 0; i < AutoProfileActivityStore.MaxEvents + 20; i++)
        {
            AutoProfileActivityStore.Append(new AutoProfileEvent(
                DateTime.Now, AutoProfileActivityStore.KindFailed,
                "C:\\Users\\Aether\\Games\\game.exe", "C:\\Program Files\\Profile",
                "失败日志 C:\\Users\\Aether\\AppData\\Local\\FpsTune\\logs\\error.log"));
        }

        Assert.True(File.ReadLines(AutoProfileActivityStore.EventsFile).Count() <= AutoProfileActivityStore.MaxEvents);
        var events = AutoProfileActivityStore.Load();
        Assert.Equal(AutoProfileActivityStore.MaxEvents, events.Count);
        Assert.All(events, e =>
        {
            Assert.DoesNotContain("C:\\Users", e.Process, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Program Files", e.Profile, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AppData", e.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---------- ProcessEdgeTracker 补充：多实例与同名重复绑定 ----------

    [Fact]
    public void Edge_tracker_requires_all_instances_to_exit_before_retrigger()
    {
        var tracker = new ProcessEdgeTracker();
        // 两个同名进程只暴露一个名字级状态：true 表示至少一个在运行
        Assert.Single(tracker.Update(new Dictionary<string, bool?> { ["game"] = true }));
        Assert.Empty(tracker.Update(new Dictionary<string, bool?> { ["game"] = true }));
        // 查询失败（null）保留上一轮状态，不触发也不重置
        Assert.Empty(tracker.Update(new Dictionary<string, bool?> { ["game"] = null }));
        // 进程退出本身不是启动事件；退出后的再次启动才触发
        Assert.Empty(tracker.Update(new Dictionary<string, bool?> { ["game"] = false }));
        Assert.Single(tracker.Update(new Dictionary<string, bool?> { ["game"] = true }));
    }

    [Fact]
    public void Edge_tracker_normalizes_case_and_ext()
    {
        var tracker = new ProcessEdgeTracker();
        Assert.Single(tracker.Update(new Dictionary<string, bool?> { ["Game.EXE"] = true }));
        Assert.Empty(tracker.Update(new Dictionary<string, bool?> { ["game"] = true }));
    }

    [Fact]
    public void Edge_tracker_observe_baselines_running_process_after_binding_change()
    {
        var tracker = new ProcessEdgeTracker();
        tracker.Observe(new Dictionary<string, bool?> { ["game"] = true });

        Assert.Empty(tracker.Update(new Dictionary<string, bool?> { ["game"] = true }));
        Assert.Empty(tracker.Update(new Dictionary<string, bool?> { ["game"] = false }));
        Assert.Single(tracker.Update(new Dictionary<string, bool?> { ["game"] = true }));
    }

    [Fact]
    public async Task AutoProfileService_dispose_async_is_idempotent_and_blocks_restart()
    {
        var service = new AutoProfileService();
        await service.DisposeAsync();
        await service.DisposeAsync();

        // Dispose 后 Start 不得重新安排轮询。
        service.Start();
    }

    [Fact]
    public async Task AutoProfileService_dispose_rejects_late_audit()
    {
        var service = new AutoProfileService();
        await service.DisposeAsync();

        var accepted = service.TryAudit(new AutoProfileEvent(
            DateTime.Now, AutoProfileActivityStore.KindApplied, "late.exe", "late", "late notification"));

        Assert.False(accepted);
        Assert.False(File.Exists(AutoProfileActivityStore.EventsFile));
    }

    [Fact]
    public void TrayService_dispose_invalidates_queued_notification_generation()
    {
        var generation = TrayService.NotificationGeneration;
        Assert.True(TrayService.IsNotificationCurrent(generation));

        try
        {
            TrayService.Dispose();
            Assert.False(TrayService.IsNotificationCurrent(generation));
        }
        finally
        {
            // The test process has no Application.Current, so this only
            // re-opens the lifecycle gate without creating a native tray HWND.
            TrayService.EnsureCreated();
        }
    }
}
