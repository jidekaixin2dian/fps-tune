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
    private static readonly string[] Verbs = { "Detect", "Apply", "Restore", "ListRestore", "Experiment", "Version", "Help", "?" };

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
        // -Experiment 动词
        public bool ExpBaseline { get; set; }
        public bool ExpTest { get; set; }
        public bool ExpReport { get; set; }
        public bool Simulate { get; set; }
        public string? Group { get; set; }
        public int? Duration { get; set; }
        public string? CsvPath { get; set; }
    }

    public static bool IsCliInvocation(IReadOnlyList<string> args)
        => args.Count > 0 && ParseVerb(args[0]) is not null;

    public static int Run(string[] args)
    {
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
                "Detect" => RunDetect(options, output),
                "Apply" => RunApply(options, output),
                "Restore" => RunRestore(options, output),
                "ListRestore" => RunListRestore(options, output),
                "Experiment" => RunExperiment(options, output),
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
        output.WriteLine("FpsTune " + UpdateService.CurrentVersion);
        return 0;
    }

    private static int RunHelp(TextWriter output)
    {
        WriteUsage(output);
        return 0;
    }

    private static int RunDetect(CliOptions options, TextWriter output)
    {
        // BuildDetectJson 内部会做 -Game > 已保存路径 > 自动扫描 的回退。
        var json = DetectionService.BuildDetectJson(options.GamePath);
        if (options.Json)
        {
            output.WriteLine(json);
            return 0;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = root.GetProperty("items");
        var sb = new StringBuilder();
        sb.AppendLine($"FpsTune {UpdateService.CurrentVersion} 状态检测");
        sb.AppendLine($"管理员权限: {(root.GetProperty("admin").GetBoolean() ? "是" : "否")}");
        if (root.TryGetProperty("gamePath", out var gamePath) &&
            gamePath.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(gamePath.GetString()))
            sb.AppendLine("游戏路径: " + gamePath.GetString());
        sb.AppendLine();

        var optimized = 0;
        foreach (var item in items.EnumerateArray())
        {
            var isOptimized = item.GetProperty("optimized").GetBoolean();
            if (isOptimized)
                optimized++;
            sb.AppendLine($"{(isOptimized ? "[已优化]" : "[未优化]")} {item.GetProperty("id").GetString()}  {item.GetProperty("current").GetString()}");
        }
        sb.AppendLine();
        sb.Append($"共 {items.GetArrayLength()} 项，已优化 {optimized} 项。");
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
        // -Json 的 stdout 是机器协议，不能混入人为提示；非 JSON 模式才输出说明。
        if (!options.Json && !AdminHelper.IsAdministrator() && ids.Any(NeedsAdmin))
            output.WriteLine("提示: 当前非管理员会话，需要管理员的优化项会失败并如实报错。");

        var result = presetName is not null
            ? OptimizationEngine.ApplyPresetAsync(presetName, gamePath).GetAwaiter().GetResult()
            : OptimizationEngine.ApplyItemsAsync(ids, gamePath).GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(result.Output);
        var results = doc.RootElement.GetProperty("results");
        var failed = 0;
        foreach (var r in results.EnumerateArray())
        {
            if (!r.GetProperty("ok").GetBoolean() && !r.GetProperty("skipped").GetBoolean())
                failed++;
        }

        if (options.Json)
        {
            output.WriteLine(result.Output);
        }
        else
        {
            foreach (var r in results.EnumerateArray())
            {
                var mark = r.GetProperty("ok").GetBoolean()
                    ? (r.GetProperty("skipped").GetBoolean() ? "[跳过]" : "[成功]")
                    : "[失败]";
                output.WriteLine($"{mark} {r.GetProperty("id").GetString()} — {r.GetProperty("message").GetString()}");
            }
            output.WriteLine();
            output.WriteLine("汇总: " + doc.RootElement.GetProperty("summary").GetString());
            if (doc.RootElement.TryGetProperty("backupFile", out var backupFile) &&
                backupFile.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(backupFile.GetString()))
                output.WriteLine("备份文件: " + backupFile.GetString());
            if (doc.RootElement.TryGetProperty("reboot", out var reboot) &&
                reboot.ValueKind == JsonValueKind.Array &&
                reboot.GetArrayLength() > 0)
                output.WriteLine("需重启生效: " + string.Join("、", reboot.EnumerateArray().Select(x => x.GetString())));
        }
        return failed == 0 ? 0 : 1;
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

        var result = BackupService.RestoreAll(ids, options.BackupFile);
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
                summary = $"{result.Restored.Count} 项已还原、{result.Failures.Count} 项失败"
            };
            output.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            if (result.Restored.Count == 0 && result.Failures.Count == 0)
            {
                output.WriteLine("没有找到可还原的备份。");
            }
            else
            {
                foreach (var (file, id) in result.Restored)
                    output.WriteLine($"[已还原] {Path.GetFileName(file)}  {id}");
                foreach (var failure in result.Failures)
                    output.WriteLine("[失败] " + failure);
                output.WriteLine();
                output.WriteLine($"汇总：{result.Restored.Count} 项已还原、{result.Failures.Count} 项失败。");
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
        => ItemCatalog.All.FirstOrDefault(d => d.Id == id)?.Admin == true;

    private static string? ParseDuration(string token, CliOptions options)
    {
        if (!int.TryParse(token, out var seconds) || seconds <= 0)
            return "-Duration 必须是正整数（秒）";
        options.Duration = seconds;
        return null;
    }

    /// <summary>
    /// -Experiment：A/B 实验编排（ExperimentRunner，进程内）。
    /// 取代已删除的 tuning-experiment.ps1；-Simulate 为 dry-run，不改任何系统设置。
    /// </summary>
    private static int RunExperiment(CliOptions options, TextWriter output)
    {
        var actions = new List<string>();
        if (options.ExpBaseline) actions.Add("baseline");
        if (options.ExpTest) actions.Add("test");
        if (options.ExpReport) actions.Add("report");
        if (actions.Count != 1)
        {
            output.WriteLine("-Experiment 需要且只能选择一个动作：-Baseline | -Test -Group <group-1|group-2|group-3> | -Report。");
            return 1;
        }

        string step;
        if (options.ExpTest)
        {
            var group = (options.Group ?? "").Trim();
            if (group.Length == 0)
            {
                output.WriteLine("-Test 需要用 -Group 指定候选组: group-1 / group-2 / group-3");
                return 1;
            }
            step = group;
        }
        else
        {
            step = actions[0];
        }

        var runnerOptions = new ExperimentRunner.Options(
            Simulate: options.Simulate,
            DurationSec: options.Duration ?? 90,
            CsvPath: options.CsvPath);
        var (exitCode, json) = ExperimentRunner.RunAsync(step, runnerOptions, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (options.Json)
        {
            output.WriteLine(json);
            return exitCode;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            output.WriteLine(message.GetString());
        if (root.GetProperty("ok").GetBoolean() && actions[0] == "report")
            output.WriteLine("CSV: " + root.GetProperty("csvExport").GetString());
        return exitCode;
    }

    private static void WriteUsage(TextWriter output)
    {
        var presets = string.Join("、", OptimizationCatalog.PresetNames);
        output.WriteLine($"""
            FpsTune {UpdateService.CurrentVersion} 命令行模式

            用法:
              FpsTune.exe -Version
              FpsTune.exe -Detect [-Game <游戏exe路径>] [-Json]
              FpsTune.exe -Apply -Items <id1,id2,...> | -Preset <预设名> [-Game <游戏exe路径>] [-Json]
              FpsTune.exe -Restore [-Items <id1,id2,...>] [-BackupFile <备份文件路径>] [-Json]
              FpsTune.exe -ListRestore [-Json]
              FpsTune.exe -Experiment -Baseline | -Test -Group <group-1|group-2|group-3> | -Report [-Simulate] [-Duration <秒>] [-CsvPath <csv>] [-Json]
              FpsTune.exe -Help

            说明:
              -Json 输出机器可读 JSON（重定向/管道场景），默认输出人类可读摘要。
              -Experiment 的 A/B 实验编排（基线/候选组/报告）在本进程内完成；
              -Simulate 为 dry-run，使用模拟采样数据，不修改任何系统设置。
              预设: {presets}、full（全部 {OptimizationCatalog.ItemOrder.Count} 项）。
              -BackupFile 只还原指定的那一份备份（A/B 实验用于锚定本步骤自己的快照）；
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
                case "baseline":
                    options.ExpBaseline = true;
                    break;
                case "test":
                    options.ExpTest = true;
                    break;
                case "report":
                    options.ExpReport = true;
                    break;
                case "simulate":
                    options.Simulate = true;
                    break;
                case "group" when inline is not null:
                    options.Group = inline;
                    break;
                case "group":
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-Group 缺少值";
                    else
                        options.Group = args[++i];
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
                case "csvpath" when inline is not null:
                    options.CsvPath = inline;
                    break;
                case "csvpath":
                    if (i + 1 >= args.Length || IsFlag(args[i + 1]))
                        error = "-CsvPath 缺少值";
                    else
                        options.CsvPath = args[++i];
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
        return verb switch
        {
            "Detect" when hasApplyOnly || hasBackupFile => "-Detect 只接受 -Game 和 -Json",
            "Restore" when options.PresetSpecified || hasGame => "-Restore 只接受 -Items、-BackupFile 和 -Json",
            "ListRestore" when hasApplyOnly || hasGame || hasBackupFile => "-ListRestore 只接受 -Json",
            "Version" when hasApplyOnly || hasGame || hasBackupFile => "-Version 不接受额外参数",
            "Help" when hasApplyOnly || hasGame || hasBackupFile => "-Help 不接受额外参数",
            "?" when hasApplyOnly || hasGame || hasBackupFile => "-Help 不接受额外参数",
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
