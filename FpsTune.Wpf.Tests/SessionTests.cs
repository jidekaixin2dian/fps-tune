using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>性能会话：统计、洞察规则、持久化、损坏恢复、保留上限与导出。</summary>
public sealed class SessionTests : IDisposable
{
    private readonly string _dir;

    public SessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fpstune-tests-" + Guid.NewGuid().ToString("N"));
        PerformanceSessionStore.OverrideDir = _dir;
    }

    public void Dispose()
    {
        PerformanceSessionStore.OverrideDir = null;
        PerformanceSessionStore.DeleteFileOverride = null;
        PerformanceSessionStore.WriteActiveTombstoneOverride = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static PerformanceSession MakeSession(string id, int samples, double cpu = 50, double? gpu = 95, double? vramMib = 4000)
    {
        var start = new DateTime(2026, 9, 2, 1, 0, 0);
        var list = new List<SessionSamplePoint>();
        for (var i = 0; i < samples; i++)
            list.Add(new SessionSamplePoint(start.AddSeconds(i), cpu, 60, gpu, vramMib));
        return new PerformanceSession(id, "测试会话", start, start.AddSeconds(samples - 1),
            PerformanceSessionStore.CurrentSchemaVersion, 1, list);
    }

    // ---------- 统计 ----------

    [Fact]
    public void Statistics_computes_avg_peak_percentiles_and_skips_missing()
    {
        var samples = new List<SessionSamplePoint>
        {
            new(DateTime.Now, 10, null, null, null),
            new(DateTime.Now.AddSeconds(1), 20, 50, 70, 100),
            new(DateTime.Now.AddSeconds(2), 30, 60, 90, 200),
            new(DateTime.Now.AddSeconds(3), 40, 70, 95, 300)
        };
        var sum = SessionStatistics.Summarize(
            samples, samples[0].T, samples[^1].T);
        Assert.Equal(4, sum.SampleCount);
        Assert.Equal(samples[^1].T - samples[0].T, sum.Duration);
        Assert.NotNull(sum.Cpu);
        Assert.Equal(25, sum.Cpu!.Avg);
        Assert.Equal(40, sum.Cpu!.Peak);
        Assert.Equal(10, sum.Cpu!.LowP5);
        Assert.Equal(30, sum.Cpu!.HighP95);
        Assert.NotNull(sum.Mem);
        Assert.Equal(60, sum.Mem!.Avg);
        Assert.NotNull(sum.Gpu);
        Assert.Equal(85, sum.Gpu!.Avg);
        Assert.Equal(200, sum.VramAvgMib);
        Assert.Equal(300, sum.VramPeakMib);
    }

    [Fact]
    public void Statistics_all_missing_metric_is_null()
    {
        var samples = new List<SessionSamplePoint>
        {
            new(DateTime.Now, null, null, null, null),
            new(DateTime.Now.AddSeconds(1), null, null, null, null)
        };
        var sum = SessionStatistics.Summarize(samples, samples[0].T, samples[^1].T);
        Assert.Null(sum.Cpu);
        Assert.Null(sum.Gpu);
        Assert.Null(sum.VramAvgMib);
    }

    // ---------- 洞察（启发式，必须携带口径与缺失信号） ----------

    [Fact]
    public void Insights_flag_cpu_pressure_with_thresholds()
    {
        var samples = Enumerable.Range(0, 10).Select(i =>
            new SessionSamplePoint(DateTime.Now.AddSeconds(i), i % 2 == 0 ? 85.0 : 95.0, null, null, null)).ToList();
        var sum = SessionStatistics.Summarize(samples, samples[0].T, samples[^1].T);
        var findings = SessionInsights.Evaluate(sum);
        Assert.Contains(findings, f => f.Kind == "CpuPressure" && f.Level == "attention");
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Detail)));
    }

    [Fact]
    public void Insights_flag_vram_pressure_only_when_total_known()
    {
        var samples = Enumerable.Range(0, 5).Select(i =>
            new SessionSamplePoint(DateTime.Now.AddSeconds(i), 30.0, null, null, 9500.0)).ToList();
        var sum = SessionStatistics.Summarize(samples, samples[0].T, samples[^1].T) with { VramTotalMib = 10240 };
        var findings = SessionInsights.Evaluate(sum);
        Assert.Contains(findings, f => f.Kind == "VramPressure");

        var unknownTotal = SessionStatistics.Summarize(samples, samples[0].T, samples[^1].T);
        var findings2 = SessionInsights.Evaluate(unknownTotal);
        Assert.DoesNotContain(findings2, f => f.Kind == "VramPressure");
        Assert.Contains(findings2, f => f.Kind == "VramUnknownTotal" && f.MissingSignals.Contains("vram-total"));
    }

    [Fact]
    public void Insights_reports_missing_gpu_signal()
    {
        var samples = Enumerable.Range(0, 5).Select(i =>
            new SessionSamplePoint(DateTime.Now.AddSeconds(i), 20.0, 40.0, null, null)).ToList();
        var sum = SessionStatistics.Summarize(samples, samples[0].T, samples[^1].T);
        var findings = SessionInsights.Evaluate(sum);
        Assert.Contains(findings, f => f.Kind == "MissingSignal" && f.MissingSignals.Contains("gpu"));
    }

    [Fact]
    public void Insights_low_load_returns_no_pressure_finding()
    {
        var samples = Enumerable.Range(0, 5).Select(i =>
            new SessionSamplePoint(DateTime.Now.AddSeconds(i), 30.0, 50.0, 60.0, 1000.0)).ToList();
        var sum = SessionStatistics.Summarize(samples, samples[0].T, samples[^1].T) with { VramTotalMib = 8192 };
        var findings = SessionInsights.Evaluate(sum);
        Assert.DoesNotContain(findings, f => f.Level == "attention");
        Assert.Contains(findings, f => f.Kind == "None");
    }

    // ---------- 持久化 ----------

    [Fact]
    public void Store_save_load_roundtrip()
    {
        var session = MakeSession(PerformanceSessionStore.NewSessionId(), 3);
        PerformanceSessionStore.Save(session);
        var loaded = PerformanceSessionStore.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(session.Id, loaded[0].Id);
        Assert.Equal(session.Name, loaded[0].Name);
        Assert.Equal(session.Samples.Count, loaded[0].Samples.Count);
    }

    [Fact]
    public void Store_rejects_path_like_id_before_writing_outside_sessions_dir()
    {
        var id = "../fpstune-session-escape-" + Guid.NewGuid().ToString("N");
        var session = MakeSession(id, 1);
        Assert.Throws<ArgumentException>(() => PerformanceSessionStore.Save(session));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*.json").Any());
    }

    [Fact]
    public void Store_tolerates_corrupt_file_and_keeps_corrupt_copy()
    {
        PerformanceSessionStore.Save(MakeSession(PerformanceSessionStore.NewSessionId(), 2));
        var corruptPath = Path.Combine(_dir, PerformanceSessionStore.NewSessionId() + ".json");
        File.WriteAllText(corruptPath, "{ not valid json !!");

        var loaded = PerformanceSessionStore.LoadAll(); // 不抛异常，坏文件被跳过
        Assert.Single(loaded);
        Assert.True(File.Exists(corruptPath + ".corrupt"), "损坏文件必须留档 .corrupt");
    }

    [Fact]
    public void Store_rejects_future_schema_version_without_deleting()
    {
        var dir = Path.Combine(_dir, "future");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, new string('a', 32) + ".json");
        File.WriteAllText(file, "{\"Id\":\"" + new string('a', 32) + "\",\"Name\":\"x\",\"SchemaVersion\":99}");
        PerformanceSessionStore.OverrideDir = dir;
        try
        {
            var ok = PerformanceSessionStore.TryLoadFile(file, out var session, out var error);
            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Contains("版本", error);
            Assert.True(File.Exists(file), "版本不兼容的文件不得删除");
        }
        finally
        {
            PerformanceSessionStore.OverrideDir = _dir;
        }
    }

    [Fact]
    public void Store_preserves_structurally_invalid_json_as_corrupt()
    {
        var id = PerformanceSessionStore.NewSessionId();
        var file = Path.Combine(_dir, id + ".json");
        Directory.CreateDirectory(_dir);
        // JSON syntax is valid, but the Samples field is absent.
        File.WriteAllText(file, "{\"Id\":\"" + id + "\",\"Name\":\"x\",\"SchemaVersion\":1,\"StartedAt\":\"2026-09-02T01:00:00\",\"IntervalSeconds\":1}");

        var ok = PerformanceSessionStore.TryLoadFile(file, out var session, out var error);
        Assert.False(ok);
        Assert.Null(session);
        Assert.NotNull(error);
        Assert.True(File.Exists(file + ".corrupt"));

        var nullFile = Path.Combine(_dir, PerformanceSessionStore.NewSessionId() + ".json");
        File.WriteAllText(nullFile, "null");
        Assert.False(PerformanceSessionStore.TryLoadFile(nullFile, out _, out _));
        Assert.True(File.Exists(nullFile + ".corrupt"));
    }

    [Fact]
    public void Store_preserves_old_schema_as_corrupt_and_does_not_load_it()
    {
        var id = PerformanceSessionStore.NewSessionId();
        var file = Path.Combine(_dir, id + ".json");
        var old = MakeSession(id, 1) with { SchemaVersion = 0 };
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(old));

        var ok = PerformanceSessionStore.TryLoadFile(file, out var session, out var error);
        Assert.False(ok);
        Assert.Null(session);
        Assert.Contains("过旧", error);
        Assert.True(File.Exists(file + ".corrupt"));
    }

    [Fact]
    public void Delete_only_removes_exact_session_file()
    {
        var session = MakeSession(PerformanceSessionStore.NewSessionId(), 2);
        PerformanceSessionStore.Save(session);
        Assert.True(PerformanceSessionStore.Delete(session.Id));
        Assert.False(PerformanceSessionStore.Delete(session.Id), "重复删除应返回 false");
        Assert.False(PerformanceSessionStore.Delete("../evil"), "非法 id 拒绝");
        Assert.False(PerformanceSessionStore.Delete("short"), "非法 id 拒绝");
    }

    [Fact]
    public void Retention_keeps_newest_and_removes_only_managed_files()
    {
        for (var i = 0; i < 5; i++)
            PerformanceSessionStore.Save(MakeSession(PerformanceSessionStore.NewSessionId(), 1, cpu: 10 + i)
                with { StartedAt = DateTime.Now.AddMinutes(-i), EndedAt = DateTime.Now.AddMinutes(-i) });
        // 不该被保留策略碰到的文件
        var untouched = Path.Combine(_dir, "_active.json");
        File.WriteAllText(untouched, "{}");

        PerformanceSessionStore.EnforceRetention(max: 3);
        Assert.Equal(3, PerformanceSessionStore.LoadAll().Count);
        Assert.True(File.Exists(untouched), "非会话命名文件不得被保留策略删除");
    }

    // ---------- 中断恢复 ----------

    [Fact]
    public void Recovery_promotes_snapshot_with_enough_samples()
    {
        var snapshot = MakeSession("active-snapshot", 8);
        PerformanceSessionStore.SaveActiveSnapshot(snapshot);

        var recovered = PerformanceSessionStore.RecoverInterruptedSession(minSamples: 5);
        Assert.NotNull(recovered);
        Assert.NotEqual("active-snapshot", recovered!.Id);
        Assert.Contains("中断恢复", recovered.Name);
        Assert.False(File.Exists(PerformanceSessionStore.ActiveSessionPath), "快照在恢复后应被清除");
        Assert.Single(PerformanceSessionStore.LoadAll());
    }

    [Fact]
    public void Active_owner_blocks_second_service_recovery_and_clear()
    {
        using var first = new PerformanceSessionService();
        using var second = new PerformanceSessionService();

        Assert.True(first.Start("实例 A"));
        PerformanceSessionStore.SaveActiveSnapshotOwned(MakeSession("active-snapshot", 8));

        Assert.False(second.Start("实例 B"));
        Assert.Null(PerformanceSessionStore.RecoverInterruptedSession());
        Assert.False(PerformanceSessionStore.ClearActiveSnapshot());
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.True(File.Exists(PerformanceSessionStore.ActiveLockPath));

        first.Cancel();
        Assert.False(File.Exists(PerformanceSessionStore.ActiveLockPath));
        Assert.True(second.Start("实例 B"));
        second.Cancel();
        Assert.False(File.Exists(PerformanceSessionStore.ActiveLockPath));
    }

    [Fact]
    public void Active_owner_release_allows_recovery_after_owner_disposes()
    {
        var snapshot = MakeSession("active-snapshot", 8);
        using (var owner = PerformanceSessionStore.TryAcquireActiveSessionOwnership())
        {
            Assert.NotNull(owner);
            PerformanceSessionStore.SaveActiveSnapshotOwned(snapshot);
            Assert.Null(PerformanceSessionStore.RecoverInterruptedSession());
        }

        Assert.False(File.Exists(PerformanceSessionStore.ActiveLockPath));
        var recovered = PerformanceSessionStore.RecoverInterruptedSession();
        Assert.NotNull(recovered);
        Assert.True(PerformanceSessionStore.IsValidSessionId(recovered!.Id));
        Assert.Single(PerformanceSessionStore.LoadAll());
        Assert.False(File.Exists(PerformanceSessionStore.ActiveLockPath));
    }

    [Fact]
    public void Service_cancel_and_dispose_remove_active_lock()
    {
        var cancelled = new PerformanceSessionService();
        try
        {
            Assert.True(cancelled.Start("取消后清理锁"));
            Assert.True(File.Exists(PerformanceSessionStore.ActiveLockPath));
            cancelled.Cancel();
            Assert.False(File.Exists(PerformanceSessionStore.ActiveLockPath));
        }
        finally
        {
            cancelled.Dispose();
        }

        var disposed = new PerformanceSessionService();
        try
        {
            Assert.True(disposed.Start("退出后清理锁"));
            Assert.True(File.Exists(PerformanceSessionStore.ActiveLockPath));
        }
        finally
        {
            disposed.Dispose();
        }
        Assert.False(File.Exists(PerformanceSessionStore.ActiveLockPath));
    }

    [Fact]
    public void Service_cancel_reports_marker_write_failure_and_keeps_active()
    {
        using var service = new PerformanceSessionService();
        Assert.True(service.Start("取消标记失败"));
        PerformanceSessionStore.SaveActiveSnapshotOwned(MakeSession("active-snapshot", 8));
        PerformanceSessionStore.WriteActiveTombstoneOverride = _ => throw new IOException("simulated marker write failure");
        try
        {
            Assert.Throws<IOException>(() => service.Cancel());
        }
        finally
        {
            PerformanceSessionStore.WriteActiveTombstoneOverride = null;
        }

        Assert.False(service.IsRunning);
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.False(File.Exists(PerformanceSessionStore.ActiveCancellationPath));

        // Clean the fixture explicitly without passing through recovery.
        Assert.True(PerformanceSessionStore.MarkActiveSnapshotCancelled());
        Assert.True(PerformanceSessionStore.ClearActiveSnapshot());
        Assert.True(PerformanceSessionStore.ClearActiveCancellation());
        Assert.Empty(PerformanceSessionStore.LoadAll());
    }

    [Fact]
    public void Active_tombstone_is_durably_written_with_expected_content()
    {
        Assert.True(PerformanceSessionStore.MarkActiveSnapshotCancelled());
        Assert.Equal("cancelled\n", File.ReadAllText(PerformanceSessionStore.ActiveCancellationPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        Assert.True(PerformanceSessionStore.ClearActiveCancellation());
    }

    [Fact]
    public void Recovery_unknown_tombstone_keeps_active_and_quarantines_marker()
    {
        PerformanceSessionStore.SaveActiveSnapshot(MakeSession("active-snapshot", 8));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PerformanceSessionStore.ActiveCancellationPath, "unknown-marker\n");

        Assert.Null(PerformanceSessionStore.RecoverInterruptedSession());
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.False(File.Exists(PerformanceSessionStore.ActiveCancellationPath));
        Assert.Equal("unknown-marker\n", File.ReadAllText(PerformanceSessionStore.ActiveCancellationPath + ".corrupt"));

        Assert.True(PerformanceSessionStore.ClearActiveSnapshot());
    }

    [Fact]
    public void Future_schema_active_is_never_deleted_by_known_tombstone()
    {
        var snapshot = MakeSession("active-snapshot", 8) with { SchemaVersion = 99 };
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PerformanceSessionStore.ActiveSessionPath,
            System.Text.Json.JsonSerializer.Serialize(snapshot));
        Assert.True(PerformanceSessionStore.MarkActiveSnapshotCancelled());

        Assert.Null(PerformanceSessionStore.RecoverInterruptedSession());
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.True(File.Exists(PerformanceSessionStore.ActiveCancellationPath));

        using (var owner = PerformanceSessionStore.TryAcquireActiveSessionOwnership())
        {
            Assert.NotNull(owner);
            Assert.False(PerformanceSessionStore.PrepareForNewSession());
            Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
            Assert.True(File.Exists(PerformanceSessionStore.ActiveCancellationPath));
        }

        Assert.True(PerformanceSessionStore.ClearActiveCancellation());
        Assert.True(PerformanceSessionStore.ClearActiveSnapshot());
    }

    [Fact]
    public void Service_save_tombstone_prevents_duplicate_recovery_when_active_delete_fails()
    {
        using var service = new PerformanceSessionService();
        Assert.True(service.Start("保存后清理失败"));
        PerformanceSessionStore.SaveActiveSnapshotOwned(MakeSession("active-snapshot", 8));
        PerformanceSessionStore.DeleteFileOverride = _ => throw new IOException("simulated delete failure");
        PerformanceSession? saved;
        try
        {
            saved = service.Stop(save: true);
        }
        finally
        {
            PerformanceSessionStore.DeleteFileOverride = null;
        }

        Assert.NotNull(saved);
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.True(File.Exists(PerformanceSessionStore.ActiveCancellationPath));
        Assert.Equal("handled\n", File.ReadAllText(PerformanceSessionStore.ActiveCancellationPath));

        Assert.Null(PerformanceSessionStore.RecoverInterruptedSession());
        Assert.False(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.False(File.Exists(PerformanceSessionStore.ActiveCancellationPath));
        Assert.Single(PerformanceSessionStore.LoadAll());
        Assert.Contains(PerformanceSessionStore.LoadAll(), s => s.Id == saved!.Id);
    }

    [Fact]
    public void Service_dispose_swallows_cancel_marker_failure_and_keeps_active()
    {
        var service = new PerformanceSessionService();
        Assert.True(service.Start("退出时取消标记失败"));
        PerformanceSessionStore.SaveActiveSnapshotOwned(MakeSession("active-snapshot", 8));
        PerformanceSessionStore.WriteActiveTombstoneOverride = _ => throw new IOException("simulated marker write failure");

        try
        {
            service.Dispose();
        }
        finally
        {
            PerformanceSessionStore.WriteActiveTombstoneOverride = null;
        }

        Assert.False(service.IsRunning);
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.False(File.Exists(PerformanceSessionStore.ActiveCancellationPath));

        // The owner is released even though Dispose cannot report the failure.
        Assert.True(PerformanceSessionStore.MarkActiveSnapshotCancelled());
        Assert.True(PerformanceSessionStore.ClearActiveSnapshot());
        Assert.True(PerformanceSessionStore.ClearActiveCancellation());
    }

    [Fact]
    public void Service_cancel_tombstone_prevents_recovery_when_active_delete_fails()
    {
        using var service = new PerformanceSessionService();
        Assert.True(service.Start("可取消会话"));
        PerformanceSessionStore.SaveActiveSnapshotOwned(MakeSession("active-snapshot", 8));
        PerformanceSessionStore.DeleteFileOverride = _ => throw new IOException("simulated delete failure");
        try
        {
            service.Cancel();
        }
        finally
        {
            PerformanceSessionStore.DeleteFileOverride = null;
        }

        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.True(File.Exists(PerformanceSessionStore.ActiveCancellationPath));
        Assert.Null(PerformanceSessionStore.RecoverInterruptedSession());
        Assert.False(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.False(File.Exists(PerformanceSessionStore.ActiveCancellationPath));
        Assert.Empty(PerformanceSessionStore.LoadAll());
    }

    [Fact]
    public void Recovery_is_idempotent_when_active_cleanup_fails()
    {
        PerformanceSessionStore.SaveActiveSnapshot(MakeSession("active-snapshot", 8));
        PerformanceSessionStore.DeleteFileOverride = _ => throw new IOException("simulated delete failure");

        PerformanceSession? first;
        PerformanceSession? second;
        try
        {
            first = PerformanceSessionStore.RecoverInterruptedSession(minSamples: 5);
            second = PerformanceSessionStore.RecoverInterruptedSession(minSamples: 5);
        }
        finally
        {
            PerformanceSessionStore.DeleteFileOverride = null;
        }

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.True(PerformanceSessionStore.IsValidSessionId(first.Id));
        Assert.Single(PerformanceSessionStore.LoadAll());
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));

        // Once deletion is available again, the existing deterministic copy is
        // recognized and only the active marker is cleared.
        var cleaned = PerformanceSessionStore.RecoverInterruptedSession(minSamples: 5);
        Assert.Equal(first.Id, cleaned?.Id);
        Assert.False(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.Single(PerformanceSessionStore.LoadAll());
    }

    [Fact]
    public void Recovery_discards_snapshot_with_too_few_samples()
    {
        PerformanceSessionStore.SaveActiveSnapshot(MakeSession("active-snapshot", 3));
        var recovered = PerformanceSessionStore.RecoverInterruptedSession(minSamples: 5);
        Assert.Null(recovered);
        Assert.False(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.Empty(PerformanceSessionStore.LoadAll());
    }

    [Fact]
    public void Recovery_does_not_promote_future_schema_snapshot_or_delete_it()
    {
        var snapshot = MakeSession("active-snapshot", 8) with { SchemaVersion = 99 };
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PerformanceSessionStore.ActiveSessionPath,
            System.Text.Json.JsonSerializer.Serialize(snapshot));

        Assert.Null(PerformanceSessionStore.RecoverInterruptedSession(minSamples: 5));
        Assert.True(File.Exists(PerformanceSessionStore.ActiveSessionPath));
        Assert.Empty(PerformanceSessionStore.LoadAll());
    }

    // ---------- 导出 ----------

    [Fact]
    public void Export_csv_has_header_units_and_empty_cells_for_missing()
    {
        var session = new PerformanceSession("id", "会话", DateTime.Now, DateTime.Now, 1, 1,
            new List<SessionSamplePoint>
            {
                new(new DateTime(2026, 9, 2, 1, 0, 0), 50, 60, null, null),
                new(new DateTime(2026, 9, 2, 1, 0, 1), 51.5, 61, 80, 2048)
            });
        var csv = SessionExporter.BuildCsv(session);
        var lines = csv.Split('\n');
        Assert.Equal("schema_version,timestamp_iso,cpu_pct,mem_pct,gpu_pct,vram_used_mib", lines[0].TrimEnd('\r'));
        Assert.Equal("1,2026-09-02T01:00:00.0000000,50,60,,", lines[1].TrimEnd('\r'));
        Assert.Contains("51.5", lines[2]);
        Assert.Contains("2048", lines[2]);
    }

    [Fact]
    public void Export_json_has_schema_version_and_units()
    {
        var session = MakeSession(PerformanceSessionStore.NewSessionId(), 2);
        using var doc = System.Text.Json.JsonDocument.Parse(SessionExporter.BuildJson(session));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("units", out _));
        Assert.Equal(session.Id, root.GetProperty("session").GetProperty("Id").GetString());
    }

    [Fact]
    public void Export_json_scrubs_path_like_session_name_and_writes_atomically()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var session = MakeSession(PerformanceSessionStore.NewSessionId(), 1) with
        {
            Name = "路径 " + profile + "\\game"
        };
        var path = Path.Combine(_dir, "export.json");

        SessionExporter.ExportJson(session, path);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain(profile, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".tmp", string.Join("\n", Directory.GetFiles(_dir)));
    }

    [Fact]
    public void Session_roundtrip_keeps_vram_total_for_offline_insights()
    {
        var session = MakeSession(PerformanceSessionStore.NewSessionId(), 3) with { VramTotalMib = 12227 };
        PerformanceSessionStore.Save(session);
        var loaded = PerformanceSessionStore.LoadAll().Single();
        Assert.Equal(12227, loaded.VramTotalMib);
        var sum = SessionStatistics.Summarize(loaded);
        Assert.Equal(12227, sum.VramTotalMib); // 洞察可据此计算占比
        var findings = SessionInsights.Evaluate(sum);
        Assert.DoesNotContain(findings, f => f.Kind == "VramUnknownTotal");
    }

    // ---------- 会话服务 ----------

    [Fact]
    public void Service_start_stop_saves_and_cancel_discards()
    {
        var service = new PerformanceSessionService();
        try
        {
            service.Start("测试会话 A");
            Assert.True(service.IsRunning);
            // 首样本异步读取；立即停止时允许保存零样本会话，不能用伪造的 0 填充。
            Assert.Null(service.LatestSample);
            var saved = service.Stop(save: true);
            Assert.False(service.IsRunning);
            Assert.NotNull(saved);
            Assert.Single(PerformanceSessionStore.LoadAll());

            service.Start("测试会话 B");
            service.Cancel();
            Assert.Single(PerformanceSessionStore.LoadAll()); // 取消不落盘
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void Service_validates_session_name()
    {
        Assert.True(PerformanceSessionService.IsValidSessionName("游戏会话 1"));
        Assert.False(PerformanceSessionService.IsValidSessionName(""));
        Assert.False(PerformanceSessionService.IsValidSessionName("a/b\\c:d"));
        Assert.False(PerformanceSessionService.IsValidSessionName(new string('x', 61)));
    }
}
