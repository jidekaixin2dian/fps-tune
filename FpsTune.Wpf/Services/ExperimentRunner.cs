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
/// v2 状态绑定采样条件与实验批次；模拟与真实数据分开保存，旧记录只读兼容。
/// </summary>
public static class ExperimentRunner
{
    private const string ToolName = "fps-tune";
    private static readonly SemaphoreSlim RunGate = new(1, 1);

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
        string? GameName = null,
        IReadOnlyList<string>? Items = null,
        string? Scene = null);

    /// <summary>
    /// 执行一个实验步骤。step 取值：baseline / report / group-1 / group-2 / group-3。
    /// 返回 (退出码, JSON 输出)：0 成功、1 失败——与旧脚本 CLI 语义一致。
    /// </summary>
    public static async Task<(int ExitCode, string Json)> RunAsync(
        string step, Options options, CancellationToken ct)
    {
        // 采样目标进程默认跟随当前定位的游戏主程序（红线四：全 FPS 通用）；
        // 未定位游戏时退回历史默认（三角洲）。
        var gamePath = AppState.GamePath;
        var gameName = options.GameName;
        if (string.IsNullOrWhiteSpace(gameName))
            gameName = !string.IsNullOrWhiteSpace(gamePath)
                ? Path.GetFileNameWithoutExtension(gamePath)
                : "DeltaForceClient-Win64-Shipping";
        if (!string.Equals(gameName, Path.GetFileNameWithoutExtension(gamePath ?? ""), StringComparison.OrdinalIgnoreCase))
            gamePath = null;
        options = options with { GameName = gameName };

        var (mode, groupId) = step switch
        {
            "baseline" => ("baseline", null),
            "report" => ("report", null),
            var g when Groups.Any(x => x.Id == g) => ("test", g),
            // 0.2.0 M1：自定义实测组——Options.Items 提供项集合（整套实测主模式 / 单项深测 size-1）
            "custom" => ("test", "custom"),
            _ => ("unknown", null),
        };
        if (mode == "unknown")
            return (1, ErrorJson("test", $"未知实验步骤: {step}（可选 baseline / report / group-1 / group-2 / group-3）"));

        await RunGate.WaitAsync(ct);
        try
        {
            if (!options.Simulate && mode != "report" && HasInterruptedOperation())
                return (1, ErrorJson(mode, Str.T("Str.ExperimentInterrupted")));
            JsonObject result = mode switch
            {
                "baseline" => await RunBaselineAsync(options, ct, gamePath),
                "report" => RunReport(options),
                _ => await RunTestGroupAsync(groupId!, options, ct, gamePath),
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
        finally { RunGate.Release(); }
    }

    private static string ErrorJson(string mode, string error) =>
        new JsonObject { ["tool"] = ToolName, ["version"] = UpdateService.CurrentVersion, ["mode"] = mode, ["ok"] = false, ["error"] = error }
            .ToJsonString(JsonOpts);

    // ------------------------------------------------------------------
    // 基线
    // ------------------------------------------------------------------

    private static async Task<JsonObject> RunBaselineAsync(Options options, CancellationToken ct, string? gamePath)
    {
        var samples = await CollectSamplesAsync(3, options, ct);
        if (samples.Ok is false)
            return Fail("baseline", samples.Error!);
        var summary = Summarize(samples.Samples!);

        var context = ExperimentContext.Create(options, gamePath);
        var newState = new JsonObject
        {
            ["schema"] = "v2",
            ["updatedAt"] = DateTime.Now.ToString("o"),
            ["baseline"] = new JsonObject
            {
                ["samples"] = SamplesJson(samples.Samples!),
                ["summary"] = summary,
                ["mode"] = samples.Mode,
                ["durationSec"] = options.DurationSec,
                ["context"] = JsonSerializer.SerializeToNode(context),
            },
            ["groups"] = new JsonArray(),
        };
        WriteState(newState, options);
        AppendHistory(new JsonObject
        {
            ["time"] = DateTime.Now.ToString("o"),
            ["kind"] = "baseline",
            ["samplerMode"] = samples.Mode,
            ["experimentId"] = context.BatchId,
            ["id"] = "baseline",
            ["name"] = "基线",
            ["summary"] = summary.DeepClone(),
        }, options);

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

    private static async Task<JsonObject> RunTestGroupAsync(string groupId, Options options, CancellationToken ct, string? gamePath)
    {
        // 0.2.0 M1：已知组沿用硬编码清单；"custom" 组从 Options.Items 取项集合。
        var known = Groups.FirstOrDefault(g => g.Id == groupId);
        var isKnownGroup = known.Id is not null;
        var itemIds = isKnownGroup ? known.Items : (options.Items?.ToArray() ?? []);
        if (!isKnownGroup)
        {
            if (itemIds.Length == 0)
                return Fail("test", Str.T("Str.CustomItemsRequired"));
            var unknown = itemIds.Where(id => ItemCatalog.All.All(x => x.Id != id)).ToList();
            if (unknown.Count > 0)
                return Fail("test", Str.T("Str.CustomUnknownItems", string.Join(", ", unknown)));
            var rebootIds = itemIds
                .Where(id => ItemCatalog.All.First(x => x.Id == id).Reboot)
                .ToList();
            if (rebootIds.Count > 0)
                return Fail("test", Str.T("Str.CustomRebootItems", string.Join(", ", rebootIds)));
        }
        var groupName = isKnownGroup ? known.Name : Str.T("Str.CustomGroupName", itemIds.Length);

        var state = ReadState(options);
        var baselineSummary = state?["baseline"]?["summary"] as JsonObject;
        if (baselineSummary is null)
            return Fail("test", "还没有基线数据，请先采集基线（同一地图/画质/路线采集 3 次）。");
        if (baselineSummary["stable"]?.GetValue<bool>() != true)
            return Fail("test", "基线不稳定，重新采集基线后再测试。");

        var baselineContext = state?["baseline"]?["context"]?.Deserialize<ExperimentContext>();
        if (baselineContext is null || baselineContext != ExperimentContext.Create(options, gamePath, baselineContext.BatchId))
            return Fail("test", Str.T("Str.ExperimentContextMismatch"));

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

        var inheritedIds = (state?["groups"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(g => g["keep"]?.GetValue<bool>() == true && g["id"]?.GetValue<string>() != groupId)
            .SelectMany(g => (g["items"] as JsonArray ?? []).Select(i => i!.GetValue<string>()))
            .Distinct(StringComparer.Ordinal).ToArray();
        var effectiveIds = inheritedIds.Concat(itemIds).Distinct(StringComparer.Ordinal).ToArray();

        // 1) 应用候选组（真实模式），记下"本次应用写下的快照"与"真正被改动的项"
        string? appliedBackup = null;
        var changedIds = itemIds.ToList();
        if (!options.Simulate)
        {
            WriteInFlight(groupId, itemIds, null);
            var applied = await ApplyGroupAsync(itemIds, ct, gamePath);
            appliedBackup = applied.BackupFile;
            changedIds = applied.ChangedIds;
            WriteInFlight(groupId, changedIds, appliedBackup);
            if (!applied.Ok)
            {
                var rollback = changedIds.Count == 0 ? null : await RestoreGroupAsync(changedIds, appliedBackup);
                if (rollback is null || rollback.Ok) ClearInFlight();
                return Fail("test", Str.T("Str.ExperimentApplyFailed", applied.Error, rollback?.Error ?? ""));
            }
            if (string.IsNullOrEmpty(appliedBackup) || !File.Exists(appliedBackup))
                return Fail("test",
                    "应用已完成，但没有取得本步骤自己的备份快照，已停止采样以保证可还原；请在「还原」页核对该组项目后重试。");
        }

        try
        {
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
            if (!options.Simulate) ClearInFlight();
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

        // 0.2.0 M1：判定沉淀——真实模式（非 Simulate）的每次实验结论入册 verdicts.json；
        // 模拟数据绝不入册（红线「数据说话」）。写入失败静默，不阻塞实验流程。
        if (!options.Simulate && summary["stable"]?.GetValue<bool>() == true && baselineSummary["avgFps"] is { } baseAvgNode && baseAvgNode.GetValue<double>() > 0)
        {
            var baseAvg = baseAvgNode.GetValue<double>();
            var testAvg = summary["avgFps"]!.GetValue<double>();
            VerdictStore.Upsert(new VerdictStore.VerdictEntry(
                VerdictStore.MakeKey(effectiveIds),
                Kind: effectiveIds.Length == 1 ? "item" : "bundle",
                Items: effectiveIds,
                GroupId: groupId,
                Game: options.GameName ?? "",
                AvgFpsBase: baseAvg,
                AvgFpsTest: testAvg,
                P1LowBase: baselineSummary["p1Low"]!.GetValue<double>(),
                P1LowTest: summary["p1Low"]!.GetValue<double>(),
                StuttersBase: baselineSummary["stutters"]!.GetValue<int>(),
                StuttersTest: summary["stutters"]!.GetValue<int>(),
                Stable: true,
                Keep: kept,
                DeltaPct: Math.Round((testAvg - baseAvg) / baseAvg * 100, 2),
                At: DateTime.Now,
                DurationSec: options.DurationSec,
                Mode: samples.Mode ?? "auto",
                GamePath: gamePath,
                RuleVersion: "v2-p99-3",
                ExperimentId: baselineContext.BatchId,
                P99Base: baselineSummary["p99Ms"]!.GetValue<double>(),
                P99Test: summary["p99Ms"]!.GetValue<double>(),
                Reverted: reverted,
                RecoveryError: revertError,
                EnvironmentHash: baselineContext.EnvironmentHash,
                RecipeHash: baselineContext.RecipeHash,
                Scene: baselineContext.Scene));
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
            ["id"] = groupId,
            ["name"] = groupName,
            ["items"] = new JsonArray(itemIds.Select(i => JsonValue.Create(i)).ToArray()),
            ["effectiveItems"] = new JsonArray(effectiveIds.Select(i => JsonValue.Create(i)).ToArray()),
            ["backupFile"] = appliedBackup,
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
            ["schema"] = "v2",
            ["updatedAt"] = DateTime.Now.ToString("o"),
            ["baseline"] = state!["baseline"]!.DeepClone(),
            ["groups"] = groups,
        }, options);
        AppendHistory(new JsonObject
        {
            ["time"] = DateTime.Now.ToString("o"),
            ["kind"] = "test",
            ["samplerMode"] = samples.Mode,
            ["experimentId"] = baselineContext.BatchId,
            ["id"] = groupId,
            ["name"] = groupName,
            ["summary"] = summary.DeepClone(),
            ["keep"] = kept,
            ["reverted"] = reverted,
            ["reason"] = reason,
        }, options);

        if (!options.Simulate && (revertError.Length == 0 || changedIds.Count == 0)) ClearInFlight();
        var verdict = kept ? "保留" : reverted ? "已还原" : "未还原";
        return new JsonObject
        {
            ["tool"] = ToolName,
            ["version"] = UpdateService.CurrentVersion,
            ["mode"] = "test",
            ["ok"] = true,
            ["group"] = groupId,
            ["groupName"] = groupName,
            ["items"] = new JsonArray(itemIds.Select(i => JsonValue.Create(i)).ToArray()),
            ["baseline"] = baselineSummary.DeepClone(),
            ["groupSummary"] = summary,
            ["keep"] = kept,
            ["reverted"] = reverted,
            ["revertError"] = revertError,
            ["reason"] = reason,
            ["samplerMode"] = samples.Mode,
            ["message"] = $"{groupName}（{groupId}）测试完成：{verdict}。{reason}",
        };
        }
        catch
        {
            if (!options.Simulate && appliedBackup is not null && changedIds.Count > 0)
            {
                var recovery = await RestoreGroupAsync(changedIds, appliedBackup);
                if (!recovery.Ok) throw new InvalidOperationException(Str.T("Str.ExperimentRecoveryFailed", recovery.Error));
                ClearInFlight();
            }
            throw;
        }
    }

    // ------------------------------------------------------------------
    // 报告
    // ------------------------------------------------------------------

    private static JsonObject RunReport(Options options)
    {
        var state = ReadState(options);
        if (state is null)
            return Fail("report", "还没有实验数据。");

        var csvPath = Path.Combine(StateDir(), options.Simulate ? "experiment-summary-simulated.csv" : "experiment-summary.csv");
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
            ["stateFile"] = StateFile(options),
            ["csvExport"] = csvPath,
        };
    }

    // ------------------------------------------------------------------
    // 引擎调用（进程内，不再经 FpsTune.exe 子进程）
    // ------------------------------------------------------------------

    private sealed record ApplyOutcome(bool Ok, string? Error, string? BackupFile, List<string> ChangedIds);

    private static async Task<ApplyOutcome> ApplyGroupAsync(string[] itemIds, CancellationToken ct, string? gamePath)
    {
        ct.ThrowIfCancellationRequested();
        var receipt = await OptimizationEngine.ApplyItemsWithReceiptAsync(itemIds, gamePath);
        var changed = receipt.Results.Where(r => r.Changed || r.StateUncertain).Select(r => r.Id).ToList();
        var errors = receipt.Results.Where(r => !r.Ok && !r.Skipped).Select(r => r.Id + " — " + r.Message).ToList();
        return new ApplyOutcome(errors.Count == 0, errors.Count == 0 ? null : string.Join("; ", errors), receipt.BackupFile, changed);
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
        => FindPresentMon(options.PresentMonPath);

    private static string? FindPresentMon(string? explicitPath)
    {
        if (explicitPath is { Length: > 0 })
            return Path.IsPathFullyQualified(explicitPath) && TrustedCaptureTool.IsTrusted(explicitPath) ? explicitPath : null;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WinGet\Links\presentmon.exe"),
        };
        return candidates.FirstOrDefault(p => File.Exists(p) && TrustedCaptureTool.IsTrusted(p));
    }

    /// <summary>
    /// P2-12：A/B 页前置探测 PresentMon 是否可用（只读查找，不安装、不启动）。
    /// 与自动采样共用固定安装位置及签名验证，不从 PATH 搜索。
    /// </summary>
    public static string? ProbePresentMon() => FindPresentMon(new Options());

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

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 30));
            var (code, output) = await RunProcessAsync(pm, args, timeout.Token).ConfigureAwait(false);
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
            using var verified = TrustedCaptureTool.OpenVerified(exe);
            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, []);
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
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

    /// <summary>解析 PresentMon CSV → 帧统计（JsonObject 或含 error 键）。兼容 v1/v2 列名。internal 供测试。
    /// 0.2.0 C-E：解析逻辑抽到 FrameTimeStats 共用（实验与会话同口径），此处只做 JSON 映射与中文文案。</summary>
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

        var parsed = FrameTimeStats.Parse(lines);
        if (parsed.Frame is { } frame)
        {
            return new JsonObject
            {
                ["samples"] = frame.Samples,
                ["avgFps"] = frame.AvgFps,
                ["p1Low"] = frame.P1Low,
                ["p99Ms"] = frame.P99Ms,
                ["stutters"] = frame.Stutters,
            };
        }
        // TooFewLines 沿用旧行为：返回 null（上层报"无法解析 CSV"，文案已在基线内）。
        if (parsed.Error is not { } err || err == FrameParseError.TooFewLines)
            return null;
        return new JsonObject { ["error"] = DescribeFrameParseError(err, parsed) };
    }

    private static string DescribeFrameParseError(FrameParseError err, FrameParseResult parsed) => err switch
    {
        FrameParseError.UnknownColumns => "无法识别 CSV 列名，找到的表头: " + string.Join(", ", parsed.Header),
        FrameParseError.TooFewFrames => $"有效帧样本不足（{parsed.FrameCount}）",
        // 枚举只有三个值且上方已覆盖全部已知项；到达此处说明枚举被扩展而映射漏写。
        _ => throw new UnreachableException(nameof(DescribeFrameParseError)),
    };

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
        var valid = samples.All(s => new[] { "avgFps", "p1Low", "p99Ms" }
            .All(k => s[k] is not null && double.IsFinite(s[k]!.GetValue<double>()) && s[k]!.GetValue<double>() > 0));
        var stable = valid && cv <= 0.05 && samples.Count >= 3;

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

    internal static (bool Keep, string Reason) DecideKeep(JsonObject baseline, JsonObject group)
    {
        if (baseline["stable"]?.GetValue<bool>() != true || group["stable"]?.GetValue<bool>() != true)
            return (false, Str.T("Str.ExperimentNotStable"));
        var baselineP99 = baseline["p99Ms"]!.GetValue<double>();
        var candidateP99 = group["p99Ms"]!.GetValue<double>();
        if (!double.IsFinite(baselineP99) || !double.IsFinite(candidateP99) || baselineP99 <= 0 || candidateP99 <= 0)
            return (false, Str.T("Str.ExperimentNotStable"));
        if (candidateP99 > baselineP99 * 1.03)
            return (false, Str.T("Str.ExperimentP99Rejected"));
        var bAvg = baseline["avgFps"]!.GetValue<double>();
        var bP1 = baseline["p1Low"]!.GetValue<double>();
        var gAvg = group["avgFps"]!.GetValue<double>();
        var gP1 = group["p1Low"]!.GetValue<double>();
        var bStut = baseline["stutters"]!.GetValue<int>();
        var gStut = group["stutters"]!.GetValue<int>();

        if (new[] { bAvg, bP1, gAvg, gP1 }.Any(v => !double.IsFinite(v) || v <= 0))
            return (false, Str.T("Str.ExperimentNotStable"));
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
        StateDirOverride ?? Path.Combine(UserDataPaths.Root, "experiment");

    private static JsonObject? ReadState(Options options)
    {
        var file = StateFile(options);
        if (!File.Exists(file) && !options.Simulate) file = Path.Combine(StateDir(), "state.json");
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

    private static string InFlightFile => Path.Combine(StateDir(), "in-flight-v2.json");
    private static bool HasInterruptedOperation()
    {
        if (!File.Exists(InFlightFile)) return false;
        try
        {
            var backup = JsonNode.Parse(File.ReadAllText(InFlightFile))?["backup"]?.GetValue<string>();
            if (backup is not null && BackupService.IsOperationResolved(backup))
            { ClearInFlight(); return false; }
        }
        catch { }
        return true;
    }
    private static void WriteInFlight(string group, IEnumerable<string> items, string? backup) =>
        AtomicFile.WriteAllTextDurable(InFlightFile, JsonSerializer.Serialize(new
        { group, items, backup, at = DateTimeOffset.UtcNow }), new UTF8Encoding(false));
    internal static void ClearInFlight() => File.Delete(InFlightFile);

    internal static string StateFile(Options options) => Path.Combine(StateDir(), options.Simulate ? "state-simulated-v2.json" : "state-v2.json");

    private static void WriteState(JsonObject state, Options options) =>
        AtomicFile.WriteAllTextDurable(StateFile(options), state.ToJsonString(JsonOpts), new UTF8Encoding(false));

    /// <summary>追加一条实验记录到 history.jsonl（GUI 历史趋势图的数据源）。只追加不改写。</summary>
    private static void AppendHistory(JsonObject entry, Options options)
    {
        Directory.CreateDirectory(StateDir());
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.AppendAllText(
            Path.Combine(StateDir(), options.Simulate ? "history-simulated.jsonl" : "history.jsonl"),
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
