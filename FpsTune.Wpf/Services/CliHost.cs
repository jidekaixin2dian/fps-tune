using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// FpsTune.exe 无头 CLI：命令行以已知动词开头时不进 GUI，执行完直接退出。
/// 动词与旧 PowerShell CLI（fps-tune.ps1）对齐：-Detect / -Apply / -Restore / -ListRestore / -Version。
/// GUI 子系统进程没有自己的控制台：优先使用已存在的 stdout 句柄（重定向/管道场景），
/// 挂不上控制台时静默丢弃输出（如双击启动传参）。
/// </summary>
public static class CliHost
{
    private static readonly string[] Verbs = { "Detect", "Apply", "Restore", "ListRestore", "DpcLatency", "Capture", "InstallCheck", "InstallRepair", "FilterRestore", "Version", "License", "Help", "?" };

    private sealed class CliOptions
    {
        public bool Json { get; set; }
        public bool ItemsSpecified { get; set; }
        public List<string> Items { get; } = new();
        public bool PresetSpecified { get; set; }
        public string? Preset { get; set; }
        public bool GameSpecified { get; set; }
        public string? GamePath { get; set; }
        public bool BackupFileSpecified { get; set; }
        public string? BackupFile { get; set; }
        public int? Duration { get; set; }
        // -Capture 动词
        public bool ProcessNameSpecified { get; set; }
        public string? ProcessName { get; set; }
        public bool OutSpecified { get; set; }
        public string? OutPath { get; set; }
    }

    public static bool IsCliInvocation(IReadOnlyList<string> args)
        => args.Count > 0 && ParseVerb(args[0]) is not null;

    public static int Run(string[] args)
    {
        if (args.Length > 0 && ParseVerb(args[0]) is "Detect" or "Apply")
            SettingsService.Load(readOnly: true);
        var output = CreateStdoutWriter();
        return Run(args, output);
    }

    // 供无副作用的命令行回归测试复用，避免测试启动 GUI 子系统进程或依赖控制台句柄。
    internal static int Run(string[] args, TextWriter output)
    {
        try
        {
            var verb = ParseVerb(args[0])!;
            var options = ParseOptions(args, verb, out var error);
            if (error is not null)
            {
                output.WriteLine("参数错误: " + error);
                WriteUsage(output);
                return 1;
            }

            return verb switch
            {
                "Version" => RunVersion(output),
                "License" => RunLicense(output),
                "Detect" => RunDetect(options, output),
                "Apply" => RunApply(options, output),
                "Restore" => RunRestore(options, output),
                "ListRestore" => RunListRestore(options, output),
                   "DpcLatency" => RunDpcLatency(options, output),
                "Capture" => RunCapture(options, output),
                "InstallCheck" => RunInstallCheck(options, output),
                "InstallRepair" => RunInstallRepair(options, output),
                "FilterRestore" => RunFilterRestore(options, output),
                _ => RunHelp(output),
            };
        }
        catch (Exception ex)
        {
            output.WriteLine("错误: " + ex.Message);
            return 1;
        }
    }

    private static int RunVersion(TextWriter output)
    {
        output.WriteLine("FpsTune " + UpdateService.DisplayVersion);
        return 0;
    }

    /// <summary>
    /// 登录自启动项（FilterRestoreAutostart，HKCU Run 键）的入口：只还原上次没还原干净的滤镜凭证，
    /// 不启动界面、不套任何新滤镜。0 = 没有待还原的或已全部还原；1 = 有还原失败（凭证保留）。
    /// </summary>
    private static int RunFilterRestore(CliOptions options, TextWriter output)
    {
        var restored = new List<string>();
        var errors = new List<string>();
        try
        {
            if (FullscreenPresetWatcher.RestoreOnStartup()) restored.Add("fullscreen");
        }
        catch (Exception ex) { errors.Add(PrivacyScrub.Sanitize(ex.Message)); }
        try
        {
            if (PerAppFilterExecution.RestoreOnStartup()) restored.Add("perapp");
        }
        catch (Exception ex) { errors.Add(PrivacyScrub.Sanitize(ex.Message)); }

        if (options.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { restored, errors }));
        }
        else
        {
            if (restored.Count > 0) output.WriteLine("已还原: " + string.Join("、", restored));
            else if (errors.Count == 0) output.WriteLine("没有待还原的滤镜。");
            foreach (var error in errors) output.WriteLine("还原失败: " + error);
        }
        return errors.Count == 0 ? 0 : 1;
    }

    private static int RunLicense(TextWriter output)
    {
        output.WriteLine(LicenseService.ProjectLicense);
        output.WriteLine(LicenseService.ThirdPartyNotices);
        return 0;
    }

    private static int RunHelp(TextWriter output)
    {
        WriteUsage(output);
        return 0;
    }

    private static int RunDetect(CliOptions options, TextWriter output)
    {
        var scan = DetectionService.Scan(options.GamePath);
        if (options.Json)
        {
            output.WriteLine(DetectionService.CreateSnapshot(scan).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"FpsTune {UpdateService.CurrentVersion} 状态检测");
        sb.AppendLine($"管理员权限: {(scan.Hardware.IsAdmin ? "是" : "否")}");
        if (!string.IsNullOrEmpty(scan.GamePath))
            sb.AppendLine("游戏路径: " + scan.GamePath);
        sb.AppendLine();

        foreach (var item in scan.Items)
            sb.AppendLine($"{(item.Optimized ? "[已优化]" : "[未优化]")} {item.Id}  {item.Current}");

        sb.AppendLine();
        sb.Append($"共 {scan.Items.Count} 项，已优化 {scan.Items.Count(i => i.Optimized)} 项。");
        output.WriteLine(sb.ToString());
        return 0;
    }

    private static int RunApply(CliOptions options, TextWriter output)
    {
        if (options.Items.Count == 0 && string.IsNullOrWhiteSpace(options.Preset))
        {
            output.WriteLine("-Apply 需要 -Items id1,id2 或 -Preset 预设名（二选一）。");
            return 1;
        }
        if (options.Items.Count > 0 && !string.IsNullOrWhiteSpace(options.Preset))
        {
            output.WriteLine("-Items 与 -Preset 不能同时使用。");
            return 1;
        }

        string? presetName = null;
        if (!string.IsNullOrWhiteSpace(options.Preset))
        {
            presetName = options.Preset.Trim();
            var known = OptimizationCatalog.PresetNames;
            var isFull = presetName.Equals("full", StringComparison.OrdinalIgnoreCase);
            var matched = known.FirstOrDefault(n => n.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (!isFull && matched is null)
            {
                output.WriteLine($"未知预设: {presetName}。可用: {string.Join("、", known)}、full");
                return 1;
            }
        }
        else
        {
            var unknown = options.Items.Where(id => !IsKnownItemId(id)).ToList();
            if (unknown.Count > 0)
            {
                output.WriteLine("未知优化项: " + string.Join(", ", unknown));
                return 1;
            }
        }

        var gamePath = options.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath))
            gamePath = StateStore.LoadGamePath() ?? GamePathService.Find();

        var ids = presetName is not null
            ? OptimizationCatalog.ResolvePreset(presetName)
            : options.Items;
        if (ids.Any(id => InputQueueWriter.ItemIds.Contains(id, StringComparer.OrdinalIgnoreCase)) &&
            SettingsService.LoadWarning is not null)
            throw new InvalidDataException(Str.T("Str.InputQueueSettingsUnreadable"));
        // -Json 的 stdout 是机器协议，不能混入人为提示；非 JSON 模式才输出说明。
        if (!options.Json && !AdminHelper.IsAdministrator() && ids.Any(NeedsAdmin))
            output.WriteLine("提示: 当前非管理员会话，需要管理员的优化项会失败并如实报错。");

        var receipt = NativeOptimizationEngine.ApplyItemsWithReceiptAsync(ids, gamePath).GetAwaiter().GetResult();
        var results = receipt.Results;
        var summary = $"{results.Count(r => r.Ok)} 成功、{receipt.FailedCount} 失败、{results.Count(r => r.Skipped)} 跳过";
        var reboot = receipt.RebootIds;

        if (options.Json)
        {
            var payload = new
            {
                operationId = receipt.OperationId,
                tool = "fps-tune",
                version = UpdateService.CurrentVersion,
                mode = "apply",
                results = results.Select(r => new
                {
                    id = r.Id, name = r.Name, ok = r.Ok, changed = r.Changed,
                    skipped = r.Skipped, message = r.Message, stateUncertain = r.StateUncertain
                }),
                summary,
                backupFile = receipt.BackupFile,
                reboot
            };
            output.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var r in results)
            {
                var mark = r.Ok ? (r.Skipped ? "[跳过]" : "[成功]") : "[失败]";
                output.WriteLine($"{mark} {r.Id} — {r.Message}");
            }
            output.WriteLine();
            output.WriteLine("汇总: " + summary);
            if (!string.IsNullOrEmpty(receipt.BackupFile))
                output.WriteLine("备份文件: " + receipt.BackupFile);
            if (reboot.Count > 0)
                output.WriteLine("需重启生效: " + string.Join("、", reboot));
        }
        return receipt.FailedCount == 0 ? 0 : 1;
    }

    private static int RunRestore(CliOptions options, TextWriter output)
    {
        IReadOnlyCollection<string>? ids = null;
        if (options.Items.Count > 0)
        {
            var unknown = options.Items.Where(id => !IsKnownItemId(id)).ToList();
            if (unknown.Count > 0)
            {
                output.WriteLine("未知优化项: " + string.Join(", ", unknown));
                return 1;
            }
            ids = options.Items;
        }

        var result = BackupService.RestoreSystemBackups(ids, options.BackupFile);
        if (options.Json)
        {
            var payload = new
            {
                tool = "fps-tune",
                version = UpdateService.CurrentVersion,
                mode = "restore",
                // 回显锚定文件，便于调用方核对"这次到底还原了哪份快照"
                backupFile = options.BackupFile,
                restored = result.Restored.Select(r => new { file = Path.GetFileName(r.File), id = r.Id }),
                failures = result.Failures,
                archivedStale = result.ArchivedStale,
                summary = $"{result.Restored.Count} 项已还原、{result.Failures.Count} 项失败、{result.ArchivedStale.Count} 份失效备份已归档"
            };
            output.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            if (result.Restored.Count == 0 && result.Failures.Count == 0 && result.ArchivedStale.Count == 0)
            {
                output.WriteLine("没有找到可还原的备份。");
            }
            else
            {
                foreach (var (file, id) in result.Restored)
                    output.WriteLine($"[已还原] {Path.GetFileName(file)}  {id}");
                foreach (var stale in result.ArchivedStale)
                    output.WriteLine($"[已归档] {stale} → .stale");
                foreach (var failure in result.Failures)
                    output.WriteLine("[失败] " + failure);
                output.WriteLine();
                output.WriteLine($"汇总：{result.Restored.Count} 项已还原、{result.Failures.Count} 项失败、{result.ArchivedStale.Count} 份失效备份已归档。");
            }
        }
        return result.Failures.Count == 0 ? 0 : 1;
    }

    private static int RunListRestore(CliOptions options, TextWriter output)
    {
        var files = BackupService.ListBackups();
        if (options.Json)
        {
            var payload = new
            {
                tool = "fps-tune",
                version = UpdateService.CurrentVersion,
                mode = "list-restore",
                count = files.Count,
                backups = files.Select(Path.GetFileName)
            };
            output.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            if (files.Count == 0)
                output.WriteLine("暂无备份。");
            foreach (var file in files)
                output.WriteLine(Path.GetFileName(file));
        }
        return 0;
    }

    private static bool IsKnownItemId(string id)
        => OptimizationCatalog.ItemOrder.Contains(id, StringComparer.Ordinal);

    private static bool NeedsAdmin(string id)
        => OptimizationCatalog.Items.FirstOrDefault(d => d.Id == id)?.Admin == true;

    private static string? ParseDuration(string token, CliOptions options)
    {
        if (!int.TryParse(token, out var seconds) || seconds <= 0)
            return "-Duration 必须是正整数（秒）";
        options.Duration = seconds;
        return null;
    }

    /// <summary>管理员子进程入口：GUI 通过 runas 调用，结果写临时 JSON，不弹控制台。</summary>
    private static int RunDpcLatency(CliOptions options, TextWriter output)
    {
        var report = DpcLatencyService.Measure(options.Duration ?? DpcLatencyService.DefaultSeconds);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(options.OutPath))
        {
            var full = Path.GetFullPath(options.OutPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json, new UTF8Encoding(false));
        }
        else
        {
            output.WriteLine(json);
        }
        return report.Ok ? 0 : 1;
    }

    /// <summary>
    /// -Capture：定量采样一次（帧时间 + 显卡遥测 + 整机后台负载），run.json 与 frames.csv
    /// 落盘到用户数据 captures 目录；-Out 可另存一份 JSON。失败时 JSON 带 error/reasonCode，退出码 1。
    /// </summary>
    private static int RunCapture(CliOptions options, TextWriter output)
    {
        // ValidateOptionsForVerb 已保证 -Game / -ProcessName 二选一且给出的值有效。
        string processName;
        string? gamePath = null;
        if (options.GameSpecified)
        {
            gamePath = options.GamePath;
            processName = Path.GetFileNameWithoutExtension(gamePath)!;
        }
        else
        {
            processName = options.ProcessName!;
        }
        var request = new CaptureRequest(processName, GamePath: gamePath, DurationSec: options.Duration ?? 60);
        // CLI 由 App.OnStartup 在 UI 线程同步执行：把异步链放到线程池再同步等，避免卡在 DispatcherSynchronizationContext。
        var run = Task.Run(() => CaptureOrchestrator.CaptureAsync(request, CancellationToken.None))
            .GetAwaiter().GetResult();
        // 采样成功时补一份延迟预算（0.5.0 M5）：输入/帧生成/显示三段，随 -Out JSON 一起输出。
        LatencyBudgetResult? budget = null;
        if (run.Error is null && File.Exists(run.CsvPath))
        {
            try
            {
                SettingsService.Load();
                var polling = SettingsService.Current.MousePollingRate;
                var series = FrameTimeColumns.Read(File.ReadLines(run.CsvPath));
                budget = LatencyBudget.Evaluate(series, polling,
                    InputQueuePolicy.RecommendedDepth(polling), LatencyBudget.ReadRefreshHz());
            }
            catch { budget = null; }
        }
        var json = JsonSerializer.Serialize(
            new { run, latencyBudget = budget }, CaptureOrchestrator.JsonOpts);
        if (!string.IsNullOrWhiteSpace(options.OutPath))
        {
            var full = Path.GetFullPath(options.OutPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json, new UTF8Encoding(false));
        }
        else
        {
            output.WriteLine(json);
        }
        if (run.Error is not null)
        {
            if (!options.Json) output.WriteLine("采样失败: " + run.Error);
            return 1;
        }
        if (!options.Json)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"采样完成: {run.Frames} 帧, 平均 {run.AvgFps:0.##} FPS, 1% low {run.P1Low:0.##}, p99 {run.P99Ms:0.##} ms, 卡顿 {run.Stutters}"));
            if (budget is not null)
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"延迟预算: 输入 {budget.InputInterval.ValueMs:0.###} ms + 队列 {budget.InputQueue.ValueMs:0.#} ms → 帧时间 p50 {budget.FrameP50.ValueMs:0.##} ms / p99 {budget.FrameP99.ValueMs:0.##} ms → 显示等待 p50 {budget.DisplayLatencyP50.ValueMs:0.##} ms ({(budget.RefreshMeasured ? $"{budget.RefreshHz:0} Hz" : "刷新率不可测")})"));
            output.WriteLine("数据已保存: " + run.JsonPath);
        }
        return 0;
    }

    /// <summary>安装信息自检（只读）。部署脚本用它核对「装着的这份」有没有被弄成孤儿目录。</summary>
    private static int RunInstallCheck(CliOptions options, TextWriter output)
    {
        var state = InstallIntegrity.Inspect();
        if (state is null)
        {
            if (options.Json) output.WriteLine("{\"managed\":false}");
            else output.WriteLine("免安装运行（目录里没有 unins000.dat），没有安装信息可查。");
            return 0;
        }

        if (options.Json)
        {
            var payload = new
            {
                managed = true,
                installDirectory = state.InstallDirectory,
                executable = state.ExecutablePath,
                complete = state.IsComplete,
                gaps = state.Details.Select(d => new { gap = d.Gap.ToString(), detail = d.Detail, strayCount = d.StrayCount }),
            };
            output.WriteLine(JsonSerializer.Serialize(payload));
        }
        else
        {
            output.WriteLine($"安装目录: {state.InstallDirectory}");
            output.WriteLine(state.IsComplete ? "安装信息: 完整" : "安装信息: 缺失 " + string.Join("、", state.Details.Select(d => d.Gap)));
        }
        return state.IsComplete ? 0 : 2;
    }

    private static int RunInstallRepair(CliOptions options, TextWriter output)
    {
        var state = InstallIntegrity.Inspect();
        if (state is null)
        {
            if (!options.Json) output.WriteLine("免安装运行，没有需要修复的安装信息。");
            return 0;
        }
        var result = InstallIntegrity.Repair(state);
        if (options.Json)
            output.WriteLine(JsonSerializer.Serialize(new { ok = result.Ok, summary = result.Summary, steps = result.Steps }));
        else
        {
            foreach (var step in result.Steps) output.WriteLine("- " + step);
            if (!result.Ok) output.WriteLine("修复失败: " + result.Summary);
        }
        return result.Ok ? 0 : 1;
    }

    private static void WriteUsage(TextWriter output)
    {
        var presets = string.Join("、", OptimizationCatalog.PresetNames);
        output.WriteLine($"""
            FpsTune {UpdateService.CurrentVersion} 命令行模式

            用法:
              FpsTune.exe -Version
              FpsTune.exe -License
              FpsTune.exe -Detect [-Game <游戏exe路径>] [-Json]
              FpsTune.exe -Apply -Items <id1,id2,...> | -Preset <预设名> [-Game <游戏exe路径>] [-Json]
              FpsTune.exe -Restore [-Items <id1,id2,...>] [-BackupFile <备份文件路径>] [-Json]
              FpsTune.exe -ListRestore [-Json]
             FpsTune.exe -DpcLatency [-Duration <秒>] [-Out <json>] [-Json]
              FpsTune.exe -Capture -Game <游戏exe路径> | -ProcessName <进程名> [-Duration <秒>] [-Out <json>] [-Json]
              FpsTune.exe -InstallCheck [-Json]
              FpsTune.exe -InstallRepair [-Json]
              FpsTune.exe -FilterRestore [-Json]
              FpsTune.exe -Help

            说明:
              -Json 输出机器可读 JSON（重定向/管道场景），默认输出人类可读摘要。
             预设: {presets}、full（全部 {OptimizationCatalog.ItemOrder.Count} 项）。
              -BackupFile 只还原指定的那一份备份；
              指定文件不合法或已被还原时会明确报错，绝不回退到其他备份。
              需要管理员的项在非管理员会话下会失败并明确报错；全部改动自动备份。
              PowerShell 交互式运行时输出可能在进程退出后才显示，重定向或 Start-Process -Wait 更可靠。
            """);
    }

    private static string? ParseVerb(string token)
    {
        var t = token.TrimStart('-', '/');
        foreach (var verb in Verbs)
            if (string.Equals(t, verb, StringComparison.OrdinalIgnoreCase))
                return verb;
        return null;
    }

    private static CliOptions ParseOptions(string[] args, string verb, out string? error)
    {
        error = null;
        var options = new CliOptions();
        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            var (name, inline) = SplitOption(token);
            switch (name.ToLowerInvariant())
            {
                case "json":
                    options.Json = true;
                    break;
                case "items" when inline is not null:
                    options.ItemsSpecified = true;
                    options.Items.AddRange(SplitIds(inline));
                    if (options.Items.Count == 0)
                        error = "-Items 不能为空";
                    break;
                case "items":
                {
                    options.ItemsSpecified = true;
                    var consumed = false;
                    while (i + 1 < args.Length && !IsFlag(args[i + 1]))
                    {
                        options.Items.AddRange(SplitIds(args[++i]));
                        consumed = true;
                    }
                    if (!consumed)
                        error = "-Items 缺少值";
                    else if (options.Items.Count == 0)
                        error = "-Items 不能为空";
                    break;
                }
                case "preset" when inline is not null:
                    options.PresetSpecified = true;
                    if (string.IsNullOrWhiteSpace(inline))
                        error = "-Preset 不能为空";
                    else
                        options.Preset = inline;
                    break;
                case "preset":
                    options.PresetSpecified = true;
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-Preset 缺少值";
                    else
                        options.Preset = args[++i];
                    break;
                case "game" when inline is not null:
                    options.GameSpecified = true;
                    if (string.IsNullOrWhiteSpace(inline))
                        error = "-Game 不能为空";
                    else
                        options.GamePath = inline;
                    break;
                case "game":
                    options.GameSpecified = true;
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-Game 缺少值";
                    else
                        options.GamePath = args[++i];
                    break;
                case "backupfile" when inline is not null:
                    options.BackupFileSpecified = true;
                    if (string.IsNullOrWhiteSpace(inline))
                        error = "-BackupFile 不能为空";
                    else
                        options.BackupFile = inline;
                    break;
                case "backupfile":
                    options.BackupFileSpecified = true;
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-BackupFile 缺少值";
                    else
                        options.BackupFile = args[++i];
                    break;

                case "duration" when inline is not null:
                    error = ParseDuration(inline, options);
                    break;
                case "duration":
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-Duration 缺少值";
                    else
                        error = ParseDuration(args[++i], options);
                    break;

                case "processname" when inline is not null:
                    options.ProcessNameSpecified = true;
                    if (string.IsNullOrWhiteSpace(inline))
                        error = "-ProcessName 不能为空";
                    else
                        options.ProcessName = inline;
                    break;
                case "processname":
                    options.ProcessNameSpecified = true;
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-ProcessName 缺少值";
                    else
                        options.ProcessName = args[++i];
                    break;
                case "out" when inline is not null:
                    options.OutSpecified = true;
                    if (string.IsNullOrWhiteSpace(inline)) error = "-Out 不能为空";
                    else options.OutPath = inline;
                    break;
                case "out":
                    options.OutSpecified = true;
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-Out 缺少值";
                    else
                        options.OutPath = args[++i];
                    break;
                default:
                    error = "未知参数: " + token;
                    break;
            }

            if (error is not null)
                return options;
        }

        error = ValidateOptionsForVerb(verb, options);
        return options;
    }

    private static string? ValidateOptionsForVerb(string verb, CliOptions options)
    {
        var hasApplyOnly = options.ItemsSpecified || options.PresetSpecified;
        var hasGame = options.GameSpecified;
        var hasBackupFile = options.BackupFileSpecified;
        var hasOut = options.OutSpecified;
        if (options.GameSpecified && !FpsTune.Wpf.Core.BackupService.IsValidExecutablePath(options.GamePath))
            return Str.T("Str.CliGameInvalid");
        return verb switch
        {
            "Detect" when hasApplyOnly || hasBackupFile || hasOut => "-Detect 只接受 -Game 和 -Json",
            "Restore" when options.PresetSpecified || hasGame || hasOut => "-Restore 只接受 -Items、-BackupFile 和 -Json",
            "ListRestore" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-ListRestore 只接受 -Json",
            "DpcLatency" when hasApplyOnly || hasGame || hasBackupFile => "-DpcLatency 只接受 -Duration、-Out 和 -Json",
            "Capture" when hasApplyOnly || hasBackupFile => "-Capture 只接受 -Game、-ProcessName、-Duration、-Out 和 -Json",
            "Capture" when options.ProcessNameSpecified && hasGame => "-Game 与 -ProcessName 只能二选一",
            "Capture" when !options.ProcessNameSpecified && !hasGame => "-Capture 需要 -Game <游戏exe路径> 或 -ProcessName <进程名>",
            "InstallCheck" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-InstallCheck 只接受 -Json",
            "InstallRepair" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-InstallRepair 只接受 -Json",
            "FilterRestore" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-FilterRestore 只接受 -Json",
            "Version" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-Version 不接受额外参数",
            "License" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-License does not accept additional arguments",
            "Help" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-Help 不接受额外参数",
            "?" when hasApplyOnly || hasGame || hasBackupFile || hasOut => "-Help 不接受额外参数",
            _ => null
        };
    }

    private static (string Name, string? Inline) SplitOption(string token)
    {
        var t = token.TrimStart('-', '/');
        var cut = t.IndexOf('=');
        var colon = t.IndexOf(':');
        if (cut < 0 || (colon >= 0 && colon < cut))
            cut = colon;
        if (cut == 0)
            return ("", null);
        if (cut > 0)
            return (t[..cut], t[(cut + 1)..]);
        return (t, null);
    }

    private static IEnumerable<string> SplitIds(string value)
        => value.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0);

    private static bool IsFlag(string token) => token.StartsWith('-') || token.StartsWith('/');

    // GUI 子系统进程没有控制台；重定向时标准句柄已存在，直接用。
    // 无句柄时尽力挂到父控制台并打开 CONOUT$（AttachConsole 不会自动更新标准句柄）。
    private static TextWriter CreateStdoutWriter()
    {
        try
        {
            var handle = GetStdHandle(-11 /* STD_OUTPUT_HANDLE */);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                var stream = Console.OpenStandardOutput();
                return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }

            if (AttachConsole(-1 /* ATTACH_PARENT_PROCESS */))
            {
                const uint genericWrite = 0x40000000;
                const uint fileShareReadWrite = 3;
                const uint openExisting = 3;
                var console = CreateFileW(
                    "CONOUT$", genericWrite, fileShareReadWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
                if (console != new IntPtr(-1))
                {
                    var safe = new Microsoft.Win32.SafeHandles.SafeFileHandle(console, true);
                    return new StreamWriter(new FileStream(safe, FileAccess.Write, 4096), new UTF8Encoding(false)) { AutoFlush = true };
                }
            }
            return TextWriter.Null;
        }
        catch
        {
            return TextWriter.Null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
