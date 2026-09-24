using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// A/B 实验编排器：取代 tuning-experiment.ps1（已随本类删除）。
/// 编排全部在进程内完成——应用/还原直接调 OptimizationEngine/BackupService，
/// 采样直接调 PresentMon，不再经过 PowerShell；
/// 旧脚本存在的理由（提权执行可写脚本、租约、子进程哈希复校验）随之整类删除。
/// 输出 JSON 与旧脚本逐字段同构，state.json / history.jsonl 格式不变，
/// 因此 GUI 解析、向导旧状态迁移与历史曲线均无需迁移。
/// </summary>
public static class ExperimentRunner
{
    private const string ToolName = "fps-tune";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 候选组定义（与旧脚本逐字一致）
    private static readonly (string Id, string Name, string[] Items)[] Groups =
    {
        ("group-1", "调度组", ["mmcss-games", "sys-responsiveness", "prio-separation"]),
        ("group-2", "后台组", ["net-throttling-off", "wer-off", "dvr-off"]),
        ("group-3", "电源组", ["power-ultimate"]),
    };

    // 测试注入：默认 %LOCALAPPDATA%\FpsTune\experiment
    internal static string? StateDirOverride { get; set; }

    public sealed record Options(
        bool Simulate = false,
        int DurationSec = 90,
        string? CsvPath = null,
        string? PresentMonPath = null,
        string? GameName = null);

    /// <summary>
    /// 执行一个实验步骤。step 取值：baseline / report / group-1 / group-2 / group-3。
    /// 返回 (退出码, JSON 输出)：0 成功、1 失败——与旧脚本 CLI 语义一致。
    /// </summary>
    public static async Task<(int ExitCode, string Json)> RunAsync(
        string step, Options options, CancellationToken ct)
    {
        // 采样目标进程默认跟随当前定位的游戏主程序（红线四：全 FPS 通用）；
        // 未定位游戏时退回历史默认（三角洲）。
        var gameName = options.GameName;
        if (string.IsNullOrWhiteSpace(gameName))
            gameName = !string.IsNullOrWhiteSpace(AppState.GamePath)
                ? Path.GetFileNameWithoutExtension(AppState.GamePath)
                : "DeltaForceClient-Win64-Shipping";
        options = options with { GameName = gameName };

        var (mode, groupId) = step switch
        {
            "baseline" => ("baseline", null),
            "report" => ("report", null),
            var g when Groups.Any(x => x.Id == g) => ("test", g),
            _ => ("unknown", null),
        };
        if (mode == "unknown")
            return (1, ErrorJson("test", $"未知实验步骤: {step}（可选 baseline / report / group-1 / group-2 / group-3）"));

        try
        {
            JsonObject result = mode switch
            {
                "baseline" => await RunBaselineAsync(options, ct),
                "report" => RunReport(options),
                _ => await RunTestGroupAsync(groupId!, options, ct),
            };
            var json = result.ToJsonString(JsonOpts);
            return (result["ok"]?.GetValue<bool>() == true ? 0 : 1, json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (1, ErrorJson(mode, ex.Message));
        }
    }

    private static string ErrorJson(string mode, string error) =>
        new JsonObject { ["tool"] = ToolName, ["version"] = UpdateService.CurrentVersion, ["mode"] = mode, ["ok"] = false, ["error"] = error }
            .ToJsonString(JsonOpts);

    // ------------------------------------------------------------------
    // 基线
    // ------------------------------------------------------------------

    private static async Task<JsonObject> RunBaselineAsync(Options options, CancellationToken ct)
    {
        var samples = await CollectSamplesAsync(3, options, ct);
        if (samples.Ok is false)
            return Fail("baseline", samples.Error!);
        var summary = Summarize(samples.Samples!);

        var state = ReadState();
        var newState = new JsonObject
        {
            ["schema"] = "v1",
            ["updatedAt"] = DateTime.Now.ToString("o"),
            ["baseline"] = new JsonObject
            {
                ["samples"] = SamplesJson(samples.Samples!),
                ["summary"] = summary,
                ["mode"] = samples.Mode,
                ["durationSec"] = options.DurationSec,
            },
            ["groups"] = state?["groups"] is JsonArray g ? (JsonArray)g.DeepClone() : [],
        };
        WriteState(newState);
        AppendHistory(new JsonObject
        {
            ["time"] = DateTime.Now.ToString("o"),
            ["kind"] = "baseline",
            ["id"] = "baseline",
            ["name"] = "基线",
            ["summary"] = summary.DeepClone(),
        });

        var stable = summary["stable"]!.GetValue<bool>();
        var msg = $"基线采集完成：平均帧率 {summary["avgFps"]} FPS、1% low {summary["p1Low"]}、P99 {summary["p99Ms"]} ms、卡顿 {summary["stutters"]} 次";
        msg += stable
            ? $"；稳定性达标（CV {summary["cv"]}），可以开始测试候选组。"
            : $"；稳定性不足（CV {summary["cv"]} > 0.05），请保持同一地图/画质/路线后重新采集基线。";

        return new JsonObject
        {
            ["tool"] = ToolName,
            ["version"] = UpdateService.CurrentVersion,
            ["mode"] = "baseline",
            ["ok"] = true,
            ["samplerMode"] = samples.Mode,
            ["baseline"] = newState["baseline"]!.DeepClone(),
            ["message"] = msg,
        };
    }

    // ------------------------------------------------------------------
    // 候选组测试
    // ------------------------------------------------------------------

    private static async Task<JsonObject> RunTestGroupAsync(string groupId, Options options, CancellationToken ct)
    {
        var group = Groups.First(g => g.Id == groupId);

        var state = ReadState();
        var baselineSummary = state?["baseline"]?["summary"] as JsonObject;
        if (baselineSummary is null)
            return Fail("test", "还没有基线数据，请先采集基线（同一地图/画质/路线采集 3 次）。");
        if (baselineSummary["stable"]?.GetValue<bool>() != true)
            return Fail("test", "基线不稳定，重新采集基线后再测试。");

        // 候选组必须按 group-1 → group-2 → group-3 推进；允许重跑当前组，
        // 但缺少前置组时在任何 apply、state/history/CSV 写入前明确拒绝。
        var requiredGroups = groupId switch
        {
            "group-2" => new[] { "group-1" },
            "group-3" => new[] { "group-1", "group-2" },
            _ => Array.Empty<string>(),
        };
        var completedGroups = (state?["groups"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(g => g["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        var missing = requiredGroups.Where(g => !completedGroups.Contains(g)).ToList();
        if (missing.Count > 0)
            return Fail("test",
                $"候选组顺序不合法：运行 {groupId} 前必须先完成 {string.Join("、", missing)}；当前未写入任何状态。");

        // 1) 应用候选组（真实模式），记下"本次应用写下的快照"与"真正被改动的项"
        string? appliedBackup = null;
        var changedIds = group.Items.ToList();
        if (!options.Simulate)
        {
            var applied = await ApplyGroupAsync(group.Items, ct);
            if (!applied.Ok)
                return Fail("test", "应用候选组未完成：" + applied.Error);
            appliedBackup = applied.BackupFile;
            changedIds = applied.ChangedIds;
            if (string.IsNullOrEmpty(appliedBackup) || !File.Exists(appliedBackup))
                return Fail("test",
                    "应用已完成，但没有取得本步骤自己的备份快照，已停止采样以保证可还原；请在「还原」页核对该组项目后重试。");
        }

        // 2) 采样 3 次
        SamplesResult samples;
        if (options.Simulate)
        {
            var rand = new Random(42); // 与旧脚本一致的种子：group-1 提升、group-2 下降、group-3 微升
            var baseMean = baselineSummary["avgFps"]!.GetValue<double>();
            var offset = groupId switch { "group-1" => 6.0, "group-2" => -4.0, _ => 2.5 };
            var simSamples = new List<JsonObject>();
            for (var i = 0; i < 3; i++)
            {
                var mean = baseMean + offset + (rand.NextDouble() * 2 - 1);
                simSamples.Add(new JsonObject
                {
                    ["samples"] = 5400,
                    ["avgFps"] = Math.Round(mean, 2),
                    ["p1Low"] = Math.Round(mean * 0.55 + (rand.NextDouble() * 4 - 2), 2),
                    ["p99Ms"] = Math.Round(1000.0 / (mean * 0.45) + (rand.NextDouble() * 6 - 3), 2),
                    ["stutters"] = (int)(rand.NextDouble() * 8),
                });
            }
            samples = new SamplesResult(true, Mode: "simulated", Samples: simSamples);
        }
        else
        {
            samples = await CollectSamplesAsync(3, options, ct);
        }

        if (samples.Ok is false)
        {
            // 采样失败，先按本次快照还原现场；还原失败必须如实并入错误信息。
            if (!options.Simulate)
            {
                var rr = await RestoreGroupAsync(changedIds, appliedBackup);
                if (!rr.Ok)
                    return Fail("test", samples.Error + "；且现场还原失败：" + rr.Error);
            }
            return Fail("test", samples.Error!);
        }
        var summary = Summarize(samples.Samples!);

        // 3) 决策
        var decision = DecideKeep(baselineSummary, summary);
        var kept = decision.Keep;

        // 4) 无效 → 自动还原（只还原本步骤真正改过、且记录在本次快照里的项）
        //    若本组在测试前就已达标，本次没有任何"原值"可还原，绝不能谎报"已还原"。
        var reverted = false;
        var revertError = "";
        if (!kept)
        {
            if (options.Simulate)
            {
                reverted = true;
            }
            else if (changedIds.Count == 0)
            {
                revertError = "本组项目在测试前就已全部达标，本次没有产生可还原的改动；如需回到优化前状态，请在「还原」页核对该项目更早的备份。";
            }
            else
            {
                var rr = await RestoreGroupAsync(changedIds, appliedBackup);
                if (!rr.Ok)
                    revertError = "还原失败：" + rr.Error;
                else
                {
                    var missingRestore = changedIds.Where(id => !rr.RestoredIds.Contains(id)).ToList();
                    revertError = missingRestore.Count > 0
                        ? "还原不完整，未确认还原：" + string.Join("、", missingRestore)
                        : "";
                    if (revertError.Length == 0)
                        reverted = true;
                }
            }
        }

        // 5) 更新状态
        var groups = (state?["groups"] as JsonArray ?? []).DeepClone().AsArray();
        var existing = groups.OfType<JsonObject>().FirstOrDefault(g => g["id"]?.GetValue<string>() == groupId);
        if (existing is not null)
            groups.Remove(existing);
        var reason = decision.Reason;
        if (!kept && !reverted && revertError.Length > 0)
            reason += " ⚠ " + revertError;
        groups.Add(new JsonObject
        {
            ["id"] = group.Id,
            ["name"] = group.Name,
            ["items"] = new JsonArray(group.Items.Select(i => JsonValue.Create(i)).ToArray()),
            ["appliedAt"] = DateTime.Now.ToString("o"),
            ["summary"] = summary.DeepClone(),
            ["keep"] = kept,
            ["reverted"] = reverted,
            ["revertError"] = revertError,
            ["reason"] = reason,
            ["samplerMode"] = samples.Mode,
        });
        WriteState(new JsonObject
        {
            ["schema"] = "v1",
            ["updatedAt"] = DateTime.Now.ToString("o"),
            ["baseline"] = state!["baseline"]!.DeepClone(),
            ["groups"] = groups,
        });
        AppendHistory(new JsonObject
        {
            ["time"] = DateTime.Now.ToString("o"),
            ["kind"] = "test",
            ["id"] = group.Id,
            ["name"] = group.Name,
            ["summary"] = summary.DeepClone(),
            ["keep"] = kept,
            ["reverted"] = reverted,
            ["reason"] = reason,
        });

        var verdict = kept ? "保留" : reverted ? "已还原" : "未还原";
        return new JsonObject
        {
            ["tool"] = ToolName,
            ["version"] = UpdateService.CurrentVersion,
            ["mode"] = "test",
            ["ok"] = true,
            ["group"] = group.Id,
            ["groupName"] = group.Name,
            ["items"] = new JsonArray(group.Items.Select(i => JsonValue.Create(i)).ToArray()),
            ["baseline"] = baselineSummary.DeepClone(),
            ["groupSummary"] = summary,
            ["keep"] = kept,
            ["reverted"] = reverted,
            ["revertError"] = revertError,
            ["reason"] = reason,
            ["samplerMode"] = samples.Mode,
            ["message"] = $"{group.Name}（{group.Id}）测试完成：{verdict}。{reason}",
        };
    }

    // ------------------------------------------------------------------
    // 报告
    // ------------------------------------------------------------------

    private static JsonObject RunReport(Options options)
    {
        var state = ReadState();
        if (state is null)
            return Fail("report", "还没有实验数据。");

        var csvPath = Path.Combine(StateDir(), "experiment-summary.csv");
        var lines = new List<string> { "group,keep,avgFps,p1Low,p99Ms,stutters,reason" };
        if (state["groups"] is JsonArray groups)
        {
            foreach (var g in groups.OfType<JsonObject>())
            {
                var s = g["summary"]!.AsObject();
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{g["id"]},{g["keep"]!.GetValue<bool>()},{s["avgFps"]},{s["p1Low"]},{s["p99Ms"]},{s["stutters"]},\"{g["reason"]}\""));
            }
        }
        Directory.CreateDirectory(StateDir());
        File.WriteAllLines(csvPath, lines, new UTF8Encoding(false));

        return new JsonObject
        {
            ["tool"] = ToolName,
            ["version"] = UpdateService.CurrentVersion,
            ["mode"] = "report",
            ["ok"] = true,
            ["baseline"] = state["baseline"]?["summary"]?.DeepClone(),
            ["groups"] = state["groups"] is JsonArray arr ? arr.DeepClone() : new JsonArray(),
            ["stateFile"] = Path.Combine(StateDir(), "state.json"),
            ["csvExport"] = csvPath,
        };
    }

    // ------------------------------------------------------------------
    // 引擎调用（进程内，不再经 FpsTune.exe 子进程）
    // ------------------------------------------------------------------

    private sealed record ApplyOutcome(bool Ok, string? Error, string? BackupFile, List<string> ChangedIds);

    private static async Task<ApplyOutcome> ApplyGroupAsync(string[] itemIds, CancellationToken ct)
    {
        var gamePath = StateStore.LoadGamePath() ?? GamePathService.Find();
        var run = await OptimizationEngine.ApplyItemsAsync(itemIds, gamePath);
        ct.ThrowIfCancellationRequested();

        JsonObject parsed;
        try
        {
            parsed = JsonNode.Parse(run.Output)!.AsObject();
        }
        catch
        {
            // 应用命令已经执行过，系统可能已被修改，但拿不到锚定快照，绝不能再往下采样/还原。
            return new ApplyOutcome(false,
                "应用已返回，但结果无法解析为 JSON（系统可能已被修改，请务必到「还原」页核对）：" + run.Output, null, []);
        }

        var changedIds = new List<string>();
        var failures = new List<string>();
        foreach (var r in parsed["results"]!.AsArray().OfType<JsonObject>())
        {
            var id = r["id"]!.GetValue<string>();
            if (r["changed"]?.GetValue<bool>() == true)
                changedIds.Add(id);
            if (r["ok"]?.GetValue<bool>() == false && r["skipped"]?.GetValue<bool>() == false)
                failures.Add($"{id} — {r["message"]?.GetValue<string>()}");
        }
        if (failures.Count > 0)
            return new ApplyOutcome(false, string.Join("；", failures), null, []);

        return new ApplyOutcome(true, null, parsed["backupFile"]?.GetValue<string>(), changedIds);
    }

    private sealed record RestoreOutcome(bool Ok, string? Error, HashSet<string> RestoredIds);

    private static Task<RestoreOutcome> RestoreGroupAsync(List<string> changedIds, string? backupFile)
    {
        return Task.Run(() =>
        {
            var result = BackupService.RestoreAll(changedIds, backupFile);
            var restoredIds = result.Restored.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (result.Failures.Count > 0)
                return new RestoreOutcome(false, string.Join("；", result.Failures), restoredIds);
            return new RestoreOutcome(true, null, restoredIds);
        });
    }

    // ------------------------------------------------------------------
    // PresentMon 探测与采样
    // ------------------------------------------------------------------

    private sealed record SamplesResult(bool? Ok, string? Error = null, string? Mode = null, List<JsonObject>? Samples = null);

    private static async Task<SamplesResult> CollectSamplesAsync(int count, Options options, CancellationToken ct)
    {
        var results = new List<JsonObject>();
        var mode = options.Simulate ? "simulated" : options.CsvPath is not null ? "manual" : "auto";
        if (options.Simulate)
        {
            // 模拟数据：均值 ~100 FPS、CV ~1%（稳定基线）；种子与旧脚本一致
            var rand = new Random(7);
            for (var i = 0; i < count; i++)
            {
                var mean = 100 + (rand.NextDouble() * 3 - 1.5);
                results.Add(new JsonObject
                {
                    ["samples"] = 5400,
                    ["avgFps"] = Math.Round(mean, 2),
                    ["p1Low"] = Math.Round(mean * 0.55 + (rand.NextDouble() * 2 - 1), 2),
                    ["p99Ms"] = Math.Round(1000.0 / (mean * 0.45) + (rand.NextDouble() * 4 - 2), 2),
                    ["stutters"] = (int)(8 + rand.NextDouble() * 8),
                });
            }
            return new SamplesResult(true, Mode: mode, Samples: results);
        }

        for (var i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string csv;
            if (mode == "manual")
            {
                // 手动模式只有一份 CSV，解析一次作为单次样本；
                // 不再像旧脚本那样把它当 3 次样本算出 CV=0 的假稳定。
                csv = options.CsvPath!;
                if (i > 1)
                    break;
            }
            else
            {
                csv = Path.Combine(StateDir(), $"sample-{i}.csv");
                var ok = await InvokePresentMonAutoAsync(options, options.DurationSec, csv, ct);
                if (!ok)
                    return new SamplesResult(false,
                        "PresentMon 自动采样失败（未找到 PresentMon 或游戏未运行）。请安装官方 PresentMon（winget install Intel.PresentMon.Console）后用手动模式提供采样 CSV，或用模拟模式试跑。");
            }
            var stat = ParsePresentMonCsv(csv);
            if (stat is null)
                return new SamplesResult(false, $"第 {i} 次采样解析失败: 无法解析 CSV");
            if (stat.ContainsKey("error"))
                return new SamplesResult(false, $"第 {i} 次采样解析失败: {stat["error"]!.GetValue<string>()}");
            results.Add(stat);
        }
        return new SamplesResult(true, Mode: mode, Samples: results);
    }

    private static string? FindPresentMon(Options options)
    {
        if (options.PresentMonPath is { Length: > 0 } && File.Exists(options.PresentMonPath))
            return options.PresentMonPath;

        var candidates = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(dir, "presentmon.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Links\presentmon.exe"));
        candidates.Add(@"C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe");
        candidates.Add(@"C:\Program Files (x86)\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe");
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 按进程名找游戏进程。GameName 可空（未定位游戏时为 null），
    /// 而 <c>Process.GetProcessesByName(null)</c> 会抛 ArgumentNullException，故这里先挡掉。
    /// </summary>
    private static Process? FindGameProcess(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return null;
        var p = Process.GetProcessesByName(gameName).FirstOrDefault()
                ?? Process.GetProcessesByName(gameName.Replace("-Win64-Shipping", "")).FirstOrDefault();
        return p;
    }

    /// <summary>自动调用 PresentMon 采集一次，落盘 CSV；失败返回 false。</summary>
    private static async Task<bool> InvokePresentMonAutoAsync(Options options, int seconds, string csvOut, CancellationToken ct)
    {
        var pm = FindPresentMon(options);
        if (pm is null)
            return false;

        // PresentMon 必须拿到进程名或 PID。GameName 可空（未定位游戏时为 null），
        // 这里取成非空局部变量再往下用：既挡掉空值，也让下面的参数数组推断为 string[]
        // 而不是 string?[]（否则 RunProcessAsync 处会报 CS8620）。
        var gameName = options.GameName;
        if (string.IsNullOrWhiteSpace(gameName))
            return false;
        var game = FindGameProcess(gameName);
        if (game is null)
            return false;

        // 先试官方 v2 参数（--timed / --terminate_after_timed），优先 --output_stdout（由本进程落盘，
        // 避免部分版本 --output_file 不落盘），再回退 --output_file 与 v1 参数（--duration）。
        // 使用独立 session 名，避免与 NVIDIA FrameView 服务已启动的默认 "PresentMon" 会话冲突。
        var attempts = new[]
        {
            new[] { "--session_name", "FpsTune", "--process_name", gameName, "--timed", seconds.ToString(), "--terminate_after_timed", "--no_console_stats", "--output_stdout" },
            new[] { "--session_name", "FpsTune", "--process_id", game.Id.ToString(), "--timed", seconds.ToString(), "--terminate_after_timed", "--no_console_stats", "--output_stdout" },
            new[] { "--session_name", "FpsTune", "--process_name", gameName, "--timed", seconds.ToString(), "--terminate_after_timed", "--no_console_stats", "--output_file", csvOut },
            new[] { "--session_name", "FpsTune", "--process_id", game.Id.ToString(), "--timed", seconds.ToString(), "--terminate_after_timed", "--no_console_stats", "--output_file", csvOut },
            new[] { "--process-name", gameName, "--duration", seconds.ToString(), "--output-file", csvOut },
            new[] { "--process", game.Id.ToString(), "--duration", seconds.ToString(), "--output_file", csvOut },
        };

        foreach (var args in attempts)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(csvOut))
                File.Delete(csvOut);

            var (code, output) = await RunProcessAsync(pm, args, ct).ConfigureAwait(false);
            if (code != 0)
                continue;
            if (args.Contains("--output_stdout"))
            {
                if (output.Length == 0)
                    continue;
                Directory.CreateDirectory(StateDir());
                await File.WriteAllLinesAsync(csvOut, output.Where(l => l.Length > 0), ct).ConfigureAwait(false);
                var fi = new FileInfo(csvOut);
                if (fi.Exists && fi.Length > 0)
                    return true;
            }
            else if (File.Exists(csvOut))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<(int Code, string[] Output)> RunProcessAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, []);
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            return (proc.ExitCode, stdout.Split('\n'));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (-1, []);
        }
    }

    /// <summary>解析 PresentMon CSV → 帧统计（JsonObject 或含 error 键）。兼容 v1/v2 列名。internal 供测试。</summary>
    internal static JsonObject? ParsePresentMonCsv(string csvPath)
    {
        if (!File.Exists(csvPath))
            return null;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(csvPath);
        }
        catch
        {
            return null;
        }
        if (lines.Length < 2)
            return null;

        var header = lines[0].Split(',');
        var col = -1;
        foreach (var candidate in new[] { "msBetweenPresents", "frame_time", "FPS", "fps" })
        {
            col = Array.FindIndex(header, h => h.Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (col >= 0)
                break;
        }
        if (col < 0)
        {
            var stat = new JsonObject { ["error"] = "无法识别 CSV 列名，找到的表头: " + string.Join(", ", header) };
            return stat;
        }

        var frameTimes = new List<double>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;
            var fields = line.Split(',');
            if (col >= fields.Length)
                continue;
            if (double.TryParse(fields[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
                frameTimes.Add(n);
        }
        if (frameTimes.Count < 30)
            return new JsonObject { ["error"] = $"有效帧样本不足（{frameTimes.Count}）" };

        var sorted = frameTimes.Order().ToList();
        var p99Ms = sorted[Math.Min((int)Math.Floor(sorted.Count * 0.99), sorted.Count - 1)];
        var avgMs = frameTimes.Average();
        var avgFps = 1000.0 / avgMs;
        var p1Fps = 1000.0 / p99Ms;
        var stutters = sorted.Count(v => v > 50);

        return new JsonObject
        {
            ["samples"] = frameTimes.Count,
            ["avgFps"] = Math.Round(avgFps, 2),
            ["p1Low"] = Math.Round(p1Fps, 2),
            ["p99Ms"] = Math.Round(p99Ms, 2),
            ["stutters"] = stutters,
        };
    }

    // ------------------------------------------------------------------
    // 统计与决策（公式与旧脚本一致）
    // ------------------------------------------------------------------

    private static JsonObject Summarize(List<JsonObject> samples)
    {
        var avgList = samples.Select(s => s["avgFps"]!.GetValue<double>()).ToList();
        var mean = avgList.Average();
        var sd = 0.0;
        if (avgList.Count > 1)
            sd = Math.Sqrt(avgList.Average(v => (v - mean) * (v - mean)));
        var cv = mean > 0 ? sd / mean : 1.0;

        // 手动模式只有单次样本，无法评估稳定性：如实标注且不判定为稳定（旧脚本会把同一份
        // CSV 算 3 遍得出 CV=0 的假稳定，这里一并修正）。
        var stable = cv <= 0.05 && samples.Count >= 3;

        return new JsonObject
        {
            ["avgFps"] = Math.Round(mean, 2),
            ["p1Low"] = Math.Round(samples.Average(s => s["p1Low"]!.GetValue<double>()), 2),
            ["p99Ms"] = Math.Round(samples.Average(s => s["p99Ms"]!.GetValue<double>()), 2),
            ["stutters"] = samples.Sum(s => s["stutters"]!.GetValue<int>()),
            ["cv"] = Math.Round(cv, 4),
            ["stable"] = stable,
            ["sampleCount"] = samples.Count,
        };
    }

    private static (bool Keep, string Reason) DecideKeep(JsonObject baseline, JsonObject group)
    {
        var bAvg = baseline["avgFps"]!.GetValue<double>();
        var bP1 = baseline["p1Low"]!.GetValue<double>();
        var gAvg = group["avgFps"]!.GetValue<double>();
        var gP1 = group["p1Low"]!.GetValue<double>();
        var bStut = baseline["stutters"]!.GetValue<int>();
        var gStut = group["stutters"]!.GetValue<int>();

        var dAvg = (gAvg - bAvg) / bAvg * 100.0;
        var dP1 = (gP1 - bP1) / bP1 * 100.0;
        var dStut = gStut - bStut;

        if (dAvg >= 2.0 && dP1 >= -3.0)
            return (true, $"平均帧率提升 {Math.Round(dAvg, 1)}%，1% low 未明显下降（{Math.Round(dP1, 1)}%）");
        if (dP1 >= 5.0 && dAvg >= -3.0)
            return (true, $"1% low 提升 {Math.Round(dP1, 1)}%，平均帧率未明显下降（{Math.Round(dAvg, 1)}%）");
        if (dStut <= -10 && dAvg >= -3.0)
            return (true, $"卡顿次数显著减少（{bStut} → {gStut}）且帧率未明显下降");
        return (false, $"无明显收益（平均 {Math.Round(dAvg, 1)}%、1% low {Math.Round(dP1, 1)}%）");
    }

    // ------------------------------------------------------------------
    // 状态持久化（state.json / history.jsonl，与旧脚本格式互认）
    // ------------------------------------------------------------------

    internal static string StateDir() =>
        StateDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "experiment");

    private static JsonObject? ReadState()
    {
        var file = Path.Combine(StateDir(), "state.json");
        if (!File.Exists(file))
            return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8))?.AsObject();
            // 规范化 groups 为数组（兼容旧文件里的 null / 空对象）
            if (root is not null && root["groups"] is not JsonArray)
                root["groups"] = new JsonArray();
            return root;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(JsonObject state) =>
        AtomicFile.WriteAllText(Path.Combine(StateDir(), "state.json"), state.ToJsonString(JsonOpts), new UTF8Encoding(false));

    /// <summary>追加一条实验记录到 history.jsonl（GUI 历史趋势图的数据源）。只追加不改写。</summary>
    private static void AppendHistory(JsonObject entry)
    {
        Directory.CreateDirectory(StateDir());
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.AppendAllText(
            Path.Combine(StateDir(), "history.jsonl"),
            entry.ToJsonString(opts) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static JsonArray SamplesJson(List<JsonObject> samples) =>
        new(samples.Select(s => (JsonNode)s.DeepClone()).ToArray());

    private static JsonObject Fail(string mode, string error) =>
        new()
        {
            ["tool"] = ToolName,
            ["version"] = UpdateService.CurrentVersion,
            ["mode"] = mode,
            ["ok"] = false,
            ["error"] = error,
        };
}
