using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using FpsTune.Wpf.Core;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 诊断报告导出 2.0：完全本地的诊断包（zip 内含 Markdown 摘要与机器可读 report.json）。
/// - 先在安全临时目录组装，再原子移动到用户选择的位置；
/// - 默认脱敏：用户名、用户目录、本应用数据目录与盘符路径（PrivacyScrub）；
/// - 不含联系方式，不包含凭据，不上传，不打开网络地址；
/// - 采集失败的项目明确记录"不可用/失败原因"，不伪造正常数据。
/// </summary>
public static class DiagnosticReportExporter
{
    // 测试可注入；生产代码保持默认目录。
    internal static string? BaseDirOverride { get; set; }
    internal static Func<ExperimentWizardState>? ExperimentWizardLoaderOverride { get; set; }
    internal static Func<string, FileAttributes>? FileMetadataProbeOverride { get; set; }

    private static string BaseDir => BaseDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune");

    public static string? Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出诊断报告",
            Filter = "Zip 归档 (*.zip)|*.zip",
            FileName = $"FpsTune-诊断报告-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dlg.ShowDialog() != true)
            return null;

        var target = dlg.FileName;
        try
        {
            return ExportTo(target);
        }
        catch (Exception ex)
        {
            DialogService.Warning("导出诊断报告", "导出失败，主程序不受影响：\n" + PrivacyScrub.Sanitize(ex.Message));
            return null;
        }
    }

    /// <summary>
    /// 将报告先写入目标所在目录下的随机临时目录，再在同一卷内原子替换目标。
    /// 这样跨卷移动不会把导出降级成复制/删除，也不会在替换失败时丢失旧报告。
    /// </summary>
    internal static string ExportTo(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("导出目标不能为空。", nameof(target));

        var fullTarget = Path.GetFullPath(target);
        var targetDir = Path.GetDirectoryName(fullTarget);
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new InvalidOperationException("无法确定导出目标目录。");

        Directory.CreateDirectory(targetDir);
        var stagingDir = Path.Combine(targetDir, ".fpstune-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDir);
            var stagingZip = Path.Combine(stagingDir, "report.zip");
            using (var fs = new FileStream(stagingZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var inputStatuses = new List<object>();
                AddMarkdownSummary(zip);
                AddReportJson(zip);
                AddSanitizedSettings(zip);
                AddPrivacyMinimizedStatus(Path.Combine(BaseDir, "last-detect.json"), "last-detect.json", inputStatuses);
                AddPrivacyMinimizedStatus(Path.Combine(BaseDir, "experiment", "state.json"), "experiment/state.json", inputStatuses);
                AddPrivacyMinimizedStatus(Path.Combine(BaseDir, "experiment", "history.jsonl"), "experiment/history.jsonl", inputStatuses);
                AddPrivacyMinimizedStatus(Path.Combine(BaseDir, "experiment", "wizard.json"), "experiment/wizard.json", inputStatuses);
                AddText(zip, "diagnostic-input-status.json", JsonSerializer.Serialize(new
                {
                    status = "complete",
                    files = inputStatuses
                }, new JsonSerializerOptions { WriteIndented = true }));
                AddSessionsSummary(zip);
                AddAutoProfileEvents(zip);
                AddErrorLogTail(zip);
                AddBackupManifest(zip);
            }

            MoveArchiveAtomically(stagingZip, fullTarget);
            return fullTarget;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响导出结果
            }
        }
    }

    /// <summary>同目录内使用原子替换；目标不存在时用同卷 Move，绝不先删除旧文件。</summary>
    internal static void MoveArchiveAtomically(string stagingZip, string target)
    {
        if (File.Exists(target))
            File.Replace(stagingZip, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(stagingZip, target);
    }

    // ---------- report.json（机器可读） ----------

    private static void AddReportJson(ZipArchive zip)
    {
        try
        {
            var payload = new
            {
                export = "fpstune-diagnostic-report",
                schemaVersion = 1,
                generatedAt = DateTime.Now.ToString("O"),
                appVersion = AppVersion,
                sourceRevision = SourceRevision,
                os = Environment.OSVersion.VersionString,
                is64BitOs = Environment.Is64BitOperatingSystem,
                isAdmin = AdminHelper.IsAdministrator(),
                hardware = TryGet(() =>
                {
                    var hw = HardwareInfoService.Get();
                    return new { hw.Cpu, hw.Gpu, hw.RamGB, hw.Os, hw.IsLaptop };
                }),
                vram = TryGet(() =>
                {
                    using var sampler = new MetricsSampler();
                    var s = sampler.SampleOnce();
                    return new
                    {
                        vramUsedBytes = s.VramUsedBytes,
                        vramTotalBytes = s.VramTotalBytes,
                        gpuPercent = s.GpuPercent,
                        note = s.VramUsedBytes is null ? "显存用量不可用" : null
                    };
                }),
                sessions = TryGet(() => SessionsSnapshot()),
                experiment = TryGet(() => new
                {
                    // LastRawOutput can contain arbitrary script output (up to
                    // 256 KiB), so report.json uses an explicit diagnostic
                    // projection instead of serializing the whole state.
                    wizard = ProjectWizardForDiagnostic(
                        ExperimentWizardLoaderOverride?.Invoke() ?? ExperimentWizardStore.Load()),
                    historyRuns = ExperimentHistory.Load().Count
                }),
                autoProfile = TryGet(() => new
                {
                    SettingsService.Current.AutoProfileEnabled,
                    bindings = (SettingsService.Current.AutoProfileBindings ?? [])
                        .Select(b => new { b.ProcessName, b.ProfileName, b.Enabled })
                        .ToList(),
                    recentEvents = AutoProfileActivityStore.Load().Take(20).ToList()
                }),
                backup = TryGet(() => BackupHealth()),
                settings = TryGet(() => new
                {
                    SettingsService.Current.ThemeMode,
                    SettingsService.Current.MinimizeToTray,
                    SettingsService.Current.HotkeyEnabled,
                    SettingsService.Current.NotifyOnComplete,
                    SettingsService.Current.LowSpecMode,
                    SettingsService.Current.AutoProfileEnabled,
                    // 路径字段显式脱敏；AddText 仍作为新增字段的最后一道防线。
                    gamePath = PrivacyScrub.Sanitize(StateStore.LoadGamePath() ?? "")
                }),
                notes = new[]
                {
                    "本报告仅保存在本地，默认不上传任何数据。",
                    "报告中的路径与用户名已按隐私规则脱敏。",
                    "性能数据为启发式采样结果，不代表因果。"
                }
            };
            AddText(zip, "report.json", JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        catch (Exception ex)
        {
            AddText(zip, "report.json", JsonSerializer.Serialize(new
            {
                export = "fpstune-diagnostic-report",
                error = "report.json 生成失败：" + PrivacyScrub.Sanitize(ex.Message)
            }));
        }
    }

    private static object ProjectWizardForDiagnostic(ExperimentWizardState wizard)
        => new
        {
            wizard.SchemaVersion,
            wizard.BaselineDone,
            wizard.BaselineStable,
            wizard.BaselineAvgFps,
            wizard.BaselineP1Low,
            wizard.BaselineCv,
            wizard.BaselineSessionId,
            wizard.BaselineAt,
            Groups = (wizard.Groups ?? [])
                .Where(g => g is not null)
                .Select(g => new
                {
                    g.GroupId,
                    g.Keep,
                    g.Reverted,
                    g.Reason,
                    g.AvgFps,
                    g.P1Low,
                    g.SessionId,
                    g.Simulated,
                    g.CompletedAt
                })
                .ToList(),
            wizard.ReportGenerated,
            wizard.ReportAt,
            wizard.RunningStep,
            wizard.RunningSince,
            wizard.LastError,
            wizard.LastErrorAt,
            wizard.LastErrorStep,
            CompletedSteps = wizard.CompletedSteps.ToArray(),
            wizard.NextStep,
            wizard.CurrentStep,
            rawOutputOmitted = true
        };

    /// <summary>会话摘要（最近 5 条）与失败原因。</summary>
    private static object SessionsSnapshot()
    {
        try
        {
            var sessions = PerformanceSessionStore.LoadAll().Take(5)
                .Select(s =>
                {
                    var sum = SessionStatistics.Summarize(s);
                    return new
                    {
                        s.Id,
                        s.Name,
                        s.StartedAt,
                        s.EndedAt,
                        s.IntervalSeconds,
                        sum.SampleCount,
                        cpu = sum.Cpu?.Avg,
                        mem = sum.Mem?.Avg,
                        gpu = sum.Gpu?.Avg,
                        vramAvgMib = sum.VramAvgMib,
                        vramTotalMib = sum.VramTotalMib
                    };
                })
                .ToList();
            return new { count = sessions.Count, recent = sessions };
        }
        catch (Exception ex)
        {
            return new { error = "会话摘要读取失败：" + ex.Message };
        }
    }

    private static object BackupHealth()
    {
        try
        {
            var dir = Path.Combine(BaseDir, "backup");
            if (!Directory.Exists(dir))
                return new { exists = false, fileCount = 0 };
            var files = Directory.GetFiles(dir);
            var latestFile = files.MaxBy(File.GetLastWriteTime);
            return new
            {
                exists = true,
                fileCount = files.Length,
                lastWriteTime = latestFile is not null ? File.GetLastWriteTime(latestFile) : (DateTime?)null,
                corruptLeftovers = Directory.GetFiles(BaseDir, "*.corrupt", SearchOption.AllDirectories).Length
            };
        }
        catch (Exception ex)
        {
            return new { error = "备份健康读取失败：" + ex.Message };
        }
    }

    // ---------- 概览.md（人工阅读） ----------

    private static void AddMarkdownSummary(ZipArchive zip)
    {
        var s = SettingsService.Current;
        var sb = new StringBuilder();
        sb.AppendLine("# FPS 帧律 诊断报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 程序版本：v{AppVersion}");
        sb.AppendLine($"- 源码标识：{SourceRevision}");
        sb.AppendLine($"- 操作系统：{Environment.OSVersion.VersionString}（{(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}）");
        sb.AppendLine($"- 管理员权限：{(AdminHelper.IsAdministrator() ? "是" : "否")}");

        sb.AppendLine();
        sb.AppendLine("## 硬件与显存可用性");
        sb.AppendLine();
        try
        {
            var hw = HardwareInfoService.Get();
            sb.AppendLine($"- CPU：{hw.Cpu}");
            sb.AppendLine($"- GPU：{hw.Gpu}");
            sb.AppendLine($"- 内存：{hw.RamGB} GB");
            sb.AppendLine($"- 系统：{hw.Os}");
            sb.AppendLine($"- 笔记本：{(hw.IsLaptop ? "是" : "否")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"- 硬件信息不可用：{PrivacyScrub.Sanitize(ex.Message)}");
        }
        try
        {
            using var sampler = new MetricsSampler();
            var sample = sampler.SampleOnce();
            sb.AppendLine($"- GPU 利用率（单次采样）：{(sample.GpuPercent is { } g ? g.ToString("0") + "%" : "不可用")}");
            sb.AppendLine($"- 专用显存用量：{GpuCounterMath.FormatBytesMiB(sample.VramUsedBytes)}");
            sb.AppendLine($"- 专用显存容量：{(sample.VramTotalBytes is { } t ? GpuCounterMath.FormatBytesMiB(t) : "不可用（无法从注册表读取）")}");
            if (sampler.UnavailableReasons.Count > 0)
            {
                sb.AppendLine($"- 不可用原因：{string.Join("；", sampler.UnavailableReasons.Select(kv => kv.Key + " — " + PrivacyScrub.Sanitize(kv.Value)))}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"- 显存可用性检测失败：{PrivacyScrub.Sanitize(ex.Message)}");
        }

        sb.AppendLine();
        sb.AppendLine("## 最近性能会话");
        sb.AppendLine();
        try
        {
            var sessions = PerformanceSessionStore.LoadAll().Take(5).ToList();
            if (sessions.Count == 0)
            {
                sb.AppendLine("（无会话记录）");
            }
            else
            {
                sb.AppendLine("| 会话 | 开始 | 样本 | CPU 平均 | 内存平均 | GPU 平均 | 显存平均 |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var ses in sessions)
                {
                    var sum = SessionStatistics.Summarize(ses);
                    sb.AppendLine(
                        $"| {ses.Name} | {ses.StartedAt:yyyy-MM-dd HH:mm} | {sum.SampleCount} | " +
                        $"{(sum.Cpu is { } c ? c.Avg + "%" : "不可用")} | {(sum.Mem is { } m ? m.Avg + "%" : "不可用")} | " +
                        $"{(sum.Gpu is { } g ? g.Avg + "%" : "不可用")} | {(sum.VramAvgMib is { } v ? v.ToString("0") + " MiB" : "不可用")} |");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"（会话记录读取失败：{PrivacyScrub.Sanitize(ex.Message)}）");
        }

        sb.AppendLine();
        sb.AppendLine("## A/B 实验");
        sb.AppendLine();
        try
        {
            var wizard = ExperimentWizardStore.Load();
            sb.AppendLine($"- 基线：{(wizard.BaselineDone ? $"平均 {wizard.BaselineAvgFps} FPS / 1% low {wizard.BaselineP1Low} / CV {wizard.BaselineCv}{(wizard.BaselineStable ? "（稳定）" : "（不稳定）")}" : "未完成")}");
            foreach (var g in wizard.Groups)
                sb.AppendLine($"- {WizardSteps.DisplayName(g.GroupId)}：{(g.Keep == true ? "保留" : g.Reverted ? "已还原" : "未还原")}（平均 {g.AvgFps} FPS）—— {g.Reason}");
            if (wizard.LastError is not null)
                sb.AppendLine($"- 最近错误：{wizard.LastError}（{wizard.LastErrorAt:yyyy-MM-dd HH:mm:ss}）");
            sb.AppendLine($"- 报告：{(wizard.ReportGenerated ? $"已生成（{wizard.ReportAt:yyyy-MM-dd HH:mm}）" : "未生成")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"（实验状态读取失败：{PrivacyScrub.Sanitize(ex.Message)}）");
        }

        sb.AppendLine();
        sb.AppendLine("## 自动 Profile");
        sb.AppendLine();
        sb.AppendLine($"- 功能开关：{(s.AutoProfileEnabled ? "开" : "关")}");
        try
        {
            var bindings = s.AutoProfileBindings ?? [];
            sb.AppendLine($"- 绑定数：{bindings.Count}（仅列进程名与方案名，不含路径）");
            foreach (var b in bindings)
                sb.AppendLine($"  - {b.ProcessName} → {b.ProfileName}（{(b.Enabled ? "启用" : "停用")}）");
            var events = AutoProfileActivityStore.Load().Take(10).ToList();
            sb.AppendLine($"- 近期事件（最多 10 条，共上限 {AutoProfileActivityStore.MaxEvents} 条）：");
            if (events.Count == 0)
                sb.AppendLine("  - （无）");
            foreach (var e in events)
                sb.AppendLine($"  - [{AutoProfileActivityStore.KindText(e.Kind)}] {e.Time:MM-dd HH:mm:ss} {e.Process}：{PrivacyScrub.Sanitize(e.Detail)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"（自动 Profile 状态读取失败：{PrivacyScrub.Sanitize(ex.Message)}）");
        }

        sb.AppendLine();
        sb.AppendLine("## 备份健康");
        sb.AppendLine();
        try
        {
            var dir = Path.Combine(BaseDir, "backup");
            if (!Directory.Exists(dir))
                sb.AppendLine("- 备份目录不存在（尚未执行过写系统操作）");
            else
            {
                var files = Directory.GetFiles(dir);
                sb.AppendLine($"- 备份文件数：{files.Length}");
                var latest = files.MaxBy(File.GetLastWriteTime);
                if (latest is not null)
                {
                    var latestTime = File.GetLastWriteTime(latest);
                    sb.AppendLine($"- 最近写入：{latestTime:yyyy-MM-dd HH:mm:ss}");
                }
                var corrupt = files.Count(f => f.EndsWith(".corrupt", StringComparison.OrdinalIgnoreCase));
                sb.AppendLine($"- 损坏留档数：{corrupt}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"- 备份健康读取失败：{PrivacyScrub.Sanitize(ex.Message)}");
        }

        sb.AppendLine();
        sb.AppendLine("## 设置摘要（已脱敏）");
        sb.AppendLine();
        sb.AppendLine($"- 主题：{s.ThemeMode} · 低配模式：{(s.LowSpecMode ? "开" : "关")}");
        sb.AppendLine($"- 托盘：{(s.MinimizeToTray ? "开" : "关")} · 热键：{(s.HotkeyEnabled ? "开" : "关")} · 通知：{(s.NotifyOnComplete ? "开" : "关")}");
        var gamePathText = StateStore.LoadGamePath() is { } gp ? PrivacyScrub.Sanitize(gp) : "（自动检测）";
        sb.AppendLine($"- 游戏路径：{gamePathText}");
        sb.AppendLine($"- 优化项总数：{ItemCatalog.All.Count}");

        sb.AppendLine();
        sb.AppendLine("> 本报告仅保存在本地，默认不上传任何数据；不包含联系方式等个人信息。");
        AddText(zip, "概览.md", sb.ToString());
    }

    private static void AddSanitizedSettings(ZipArchive zip)
    {
        try
        {
            var s = SettingsService.Current;
            var json = JsonSerializer.Serialize(new
            {
                s.ThemeMode,
                s.MinimizeToTray,
                s.HotkeyEnabled,
                s.NotifyOnComplete,
                s.LowSpecMode,
                s.AutoProfileEnabled,
                bindings = (s.AutoProfileBindings ?? []).Select(b => new
                {
                    b.ProcessName,
                    b.ProfileName,
                    b.Enabled
                    // ExePath 有意不导出：含用户目录路径
                }),
                GamePath = PrivacyScrub.Sanitize(StateStore.LoadGamePath() ?? "")
            }, new JsonSerializerOptions { WriteIndented = true });
            AddText(zip, "settings-sanitized.json", json);
        }
        catch (Exception ex)
        {
            AddText(zip, "settings-sanitized.json", "读取失败: " + ex.Message);
        }
    }

    private static void AddErrorLogTail(ZipArchive zip)
    {
        try
        {
            var log = Path.Combine(BaseDir, "logs", "error.log");
            if (!File.Exists(log))
            {
                AddText(zip, "error-log.txt", "(无错误日志)");
                return;
            }
            var lines = File.ReadAllLines(log, Encoding.UTF8);
            var tail = lines.Length <= 100 ? lines : lines[^100..];
            AddText(zip, "error-log.txt", PrivacyScrub.Sanitize(string.Join('\n', tail)));
        }
        catch (Exception ex)
        {
            AddText(zip, "error-log.txt", "读取失败: " + ex.Message);
        }
    }

    private static void AddBackupManifest(ZipArchive zip)
    {
        try
        {
            var dir = Path.Combine(BaseDir, "backup");
            if (!Directory.Exists(dir))
            {
                AddText(zip, "backup-manifest.txt", "(无备份目录)");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("备份文件清单（内容不含个人数据，仅记录系统改动原值）:");
            foreach (var f in Directory.GetFiles(dir))
                sb.AppendLine($"{Path.GetFileName(f)}  {new FileInfo(f).Length} bytes  {File.GetLastWriteTime(f):yyyy-MM-dd HH:mm:ss}");
            AddText(zip, "backup-manifest.txt", sb.ToString());
        }
        catch (Exception ex)
        {
            AddText(zip, "backup-manifest.txt", "读取失败: " + ex.Message);
        }
    }

    private static void AddAutoProfileEvents(ZipArchive zip)
    {
        try
        {
            var events = AutoProfileActivityStore.Load();
            if (events.Count == 0)
            {
                AddText(zip, "auto-profile-events.jsonl", "(无事件)");
                return;
            }
            var sb = new StringBuilder();
            foreach (var e in events)
                sb.AppendLine($"{e.Time:yyyy-MM-dd HH:mm:ss}\t{e.Kind}\t{e.Process}\t{e.Profile}\t{PrivacyScrub.Sanitize(e.Detail)}");
            AddText(zip, "auto-profile-events.txt", sb.ToString());
        }
        catch (Exception ex)
        {
            AddText(zip, "auto-profile-events.txt", "读取失败: " + ex.Message);
        }
    }

    private static void AddSessionsSummary(ZipArchive zip)
    {
        try
        {
            var sessions = PerformanceSessionStore.LoadAll().Take(5)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.StartedAt,
                    s.EndedAt,
                    s.IntervalSeconds,
                    Samples = s.Samples.Count,
                    Summary = SessionStatistics.Summarize(s)
                })
                .ToList();
            AddText(zip, "sessions-summary.json", JsonSerializer.Serialize(sessions,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AddText(zip, "sessions-summary.json", "读取失败: " + ex.Message);
        }
    }

    // ---------- 工具 ----------

    private static string AppVersion
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>发布构建通过 /p:SourceRevisionId 嵌入最终源码哈希；本地开发构建显示 version-only。</summary>
    public static string SourceRevision
    {
        get
        {
            try
            {
                var iv = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                return string.IsNullOrWhiteSpace(iv) ? "unknown" : iv;
            }
            catch
            {
                return "unknown";
            }
        }
    }

    private static object TryGet(Func<object> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            return new { error = "采集失败：" + ex.Message };
        }
    }

    private static void AddPrivacyMinimizedStatus(
        string path,
        string sourceName,
        ICollection<object> statuses)
    {
        try
        {
            // File.Exists suppresses access/metadata errors and would turn an
            // unreadable source into a misleading "missing" status. Attributes
            // is a metadata-only query that preserves those exceptions.
            _ = (FileMetadataProbeOverride?.Invoke(path) ?? File.GetAttributes(path));
            statuses.Add(new { source = sourceName, status = "source-present-but-omitted-by-privacy" });
        }
        catch (Exception ex)
        {
            var status = ClassifyPrivacyMetadataException(ex);
            if (status == "source-missing")
            {
                statuses.Add(new { source = sourceName, status });
            }
            else
            {
                statuses.Add(new
                {
                    source = sourceName,
                    status,
                    message = PrivacyScrub.Sanitize(ex.Message)
                });
            }
        }
    }

    internal static string ClassifyPrivacyMetadataException(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException
            ? "source-missing"
            : "metadata-check-failed";

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        // 统一兜底脱敏，避免新增摘要字段或异常分支忘记单独调用 PrivacyScrub。
        writer.Write(PrivacyScrub.Sanitize(content));
    }
}
