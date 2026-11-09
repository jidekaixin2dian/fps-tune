using System.IO.Compression;
using System.Text.Json;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 「优化设置执行失败」回归。
///
/// 现场证据：用户导出的诊断报告里 operations 是 <c>no-records</c>——应用优化失败了，却一条回执都没留，
/// 只能靠人工猜。这组用例守住三件事：
/// 1. 每次应用优化都落一条逐项回执（成功/失败/跳过 + 原因 + 阶段 + 是否管理员）；
/// 2. 部分失败写成 partial，不冒充整体失败，也不会被写成 succeeded；
/// 3. 非管理员被拦下时同样留痕，且不会先建一份空备份。
/// </summary>
[Collection("BackupService serial")]
public sealed class OptimizationFailureReceiptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-opt-receipt-" + Guid.NewGuid().ToString("N"));
    private readonly string _backupDir;

    public OptimizationFailureReceiptTests()
    {
        _backupDir = Path.Combine(_root, "backup");
        Directory.CreateDirectory(_backupDir);
        UserDataPaths.RootOverride = _root;
        BackupService.BackupDirOverride = _backupDir;
        DiagnosticReportExporter.BaseDirOverride = _root;
    }

    public void Dispose()
    {
        RegistryHelper.SnapshotOverride = null;
        RegistryHelper.SetOverride = null;
        RegistryHelper.DeleteOverride = null;
        AdminHelper.IsAdministratorOverride = null;
        DiagnosticReportExporter.BaseDirOverride = null;
        BackupService.BackupDirOverride = null;
        UserDataPaths.RootOverride = null;
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>注入一次「写到一半被系统拒绝」：KeyboardDelay 写成功、KeyboardSpeed 抛访问拒绝。</summary>
    private static void FailOnKeyboardSpeed()
    {
        var values = new Dictionary<string, object?> { ["KeyboardDelay"] = "1", ["KeyboardSpeed"] = "20" };
        RegistryHelper.SnapshotOverride = (_, _, name) =>
            new(values.ContainsKey(name), values.GetValueOrDefault(name), RegistryValueKind.String);
        RegistryHelper.SetOverride = (_, _, name, value, _) =>
        {
            if (name == "KeyboardSpeed")
                throw new UnauthorizedAccessException("Requested registry access is not allowed.");
            values[name] = value;
        };
    }

    [Fact]
    public async Task Partial_failure_keeps_per_item_receipt_and_is_not_recorded_as_success()
    {
        FailOnKeyboardSpeed();
        var run = await OptimizationEngine.ApplyItemsAsync(["keyboard-repeat", "mouse-accel-off"], null);
        Assert.Equal(1, run.ExitCode);

        var receipt = Assert.Single(OperationDiagnostics.Read(_root).Operations);
        Assert.Equal("optimize-apply", receipt.Operation);
        Assert.Equal("finalize", receipt.Stage);
        Assert.Equal("partial", receipt.Outcome);

        var items = receipt.Items ?? [];
        Assert.Equal(2, items.Count);
        var failed = items.Single(i => i.Id == "keyboard-repeat");
        Assert.Equal("failed", failed.Outcome);
        // 回执里既有人话也给原始异常，支撑时不用再让用户复现一遍。
        // 断言用 Str.T 组合同一份模板，避免把文案写死在测试里（无 Application 时两边都是 !key!）。
        Assert.Equal(
            Str.T("Str.OptItemFailureDetail", Str.T("Str.OptFailNeedAdmin"),
                "UnauthorizedAccessException: Requested registry access is not allowed."),
            failed.Message);
        Assert.Equal("succeeded", items.Single(i => i.Id == "mouse-accel-off").Outcome);
    }

    [Fact]
    public async Task All_items_failing_is_recorded_as_failed_rather_than_partial()
    {
        FailOnKeyboardSpeed();
        await OptimizationEngine.ApplyItemsAsync(["keyboard-repeat"], null);
        var receipt = Assert.Single(OperationDiagnostics.Read(_root).Operations);
        Assert.Equal("failed", receipt.Outcome);
        Assert.Equal("failed", Assert.Single(receipt.Items ?? []).Outcome);
    }

    [Fact]
    public async Task Missing_administrator_rights_is_recorded_before_any_backup_is_written()
    {
        AdminHelper.IsAdministratorOverride = () => false;
        var name = ItemCatalog.All.Single(item => item.Id == "dvr-off").DisplayName;
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => OptimizationEngine.ApplyItemsAsync(["dvr-off"], null));
        // 报错走界面文案（列出是哪几项、要什么权限），不再是过去那句英文异常。
        Assert.Equal(Str.T("Str.OptApplyNeedsAdminItems", 1, name), error.Message);
        Assert.DoesNotContain("Administrator privileges are required", error.Message);

        var receipt = Assert.Single(OperationDiagnostics.Read(_root).Operations);
        Assert.Equal("precheck", receipt.Stage);
        Assert.Equal("failed", receipt.Outcome);
        Assert.Equal(nameof(UnauthorizedAccessException), receipt.ErrorType);
        Assert.Empty(Directory.GetFiles(_backupDir, "*.json"));
    }

    [Fact]
    public async Task Diagnostic_summary_lists_the_failed_item_not_just_a_status_word()
    {
        FailOnKeyboardSpeed();
        await OptimizationEngine.ApplyItemsAsync(["keyboard-repeat"], null);

        var zipPath = DiagnosticReportExporter.ExportTo(Path.Combine(_root, "diagnostic.zip"));
        using var archive = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(archive.GetEntry("概览.md")!.Open());
        var markdown = reader.ReadToEnd();
        Assert.Contains(Str.T("Str.DiagOpsTitle"), markdown);
        Assert.Contains("optimize-apply", markdown);
        Assert.Contains(Str.T("Str.DiagOpsFailedItem", "keyboard-repeat",
            Str.T("Str.OptItemFailureDetail", Str.T("Str.OptFailNeedAdmin"),
                "UnauthorizedAccessException: Requested registry access is not allowed.")), markdown);
        Assert.Contains(Str.T("Str.DiagOpsCounts", 0, 1, 0), markdown);
    }

    [Fact]
    public void Receipts_written_before_item_support_existed_still_read()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        var legacy = new
        {
            SchemaVersion = 1,
            Operations = new[]
            {
                new
                {
                    Id = Guid.NewGuid().ToString("N"),
                    At = DateTimeOffset.UtcNow,
                    Operation = "driver-apply",
                    Target = "game.exe",
                    Stage = "finalize-backup",
                    Outcome = "succeeded",
                    IsAdministrator = false,
                    DriverVersion = "32.0.16.1062",
                    AppVersion = "0.4.3",
                    ErrorType = (string?)null,
                    NativeStatus = (int?)null,
                    Error = (string?)null,
                    Settings = Array.Empty<object>()
                }
            }
        };
        File.WriteAllText(Path.Combine(_root, "logs", "operations.json"), JsonSerializer.Serialize(legacy));
        var report = OperationDiagnostics.Read(_root);
        Assert.Equal("available", report.Status);
        Assert.Empty(Assert.Single(report.Operations).Items ?? []);
    }
}
