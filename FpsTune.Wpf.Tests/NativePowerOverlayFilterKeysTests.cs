using System.Text.Json;
using FpsTune.Wpf.Core;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// Windows 11 电源模式（overlay）与 FilterKeys 防误触的引擎/备份/还原专项。
/// 全部走注入 fake，不触碰真实电源与辅助功能状态；备份走临时目录。
/// </summary>
[Collection("BackupService serial")]
public sealed class NativePowerOverlayFilterKeysTests : IDisposable
{
    private readonly string _dir;
    private Guid _overlay = Guid.Empty;

    public NativePowerOverlayFilterKeysTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        BackupService.BackupDirOverride = _dir;
    }

    public void Dispose()
    {
        BackupService.BackupDirOverride = null;
        RegistryHelper.SnapshotOverride = null;
        NativePowerOverlay.ReadOverride = null;
        NativePowerOverlay.WriteOverride = null;
        NativePowerOverlay.SupportedOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
    }

    /// <summary>注册表读全部走 fake：接电/电池 overlay 是字符串 GUID，其余值视为不存在。</summary>
    private void FakeRegistry()
    {
        RegistryHelper.SnapshotOverride = (_, _, name) => name switch
        {
            NativePowerOverlay.AcOverlayValueName => new RegistryHelper.Snapshot(true, _overlay.ToString("D"), RegistryValueKind.String),
            NativePowerOverlay.DcOverlayValueName => new RegistryHelper.Snapshot(true, "961cc777-2547-4f9d-8174-7d86181b8a7a", RegistryValueKind.String),
            _ => new RegistryHelper.Snapshot(false, null, RegistryValueKind.None),
        };
    }

    private void FakeOverlay()
    {
        NativePowerOverlay.SupportedOverride = () => true;
        NativePowerOverlay.ReadOverride = () => _overlay;
        NativePowerOverlay.WriteOverride = guid => { _overlay = guid; return 0; };
    }

    [Fact]
    public async Task Power_overlay_apply_switches_to_best_performance_and_captures_original()
    {
        FakeRegistry(); FakeOverlay();
        var capture = BackupService.Capture(new[] { "power-overlay-perf" }, null);
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "power-overlay-perf" }, null)).Results);
        Assert.True(result.Ok); Assert.True(result.Changed);
        Assert.Equal(NativePowerOverlay.BestPerformance, _overlay);
        var record = Assert.Single(BackupService.ReadRecords(capture));
        Assert.Equal("power-overlay", record.Kind);
        Assert.Equal(Guid.Empty.ToString("D"), record.OldAcOverlayGuid);
        Assert.Equal("961cc777-2547-4f9d-8174-7d86181b8a7a", record.OldDcOverlayGuid);
    }

    [Fact]
    public async Task Power_overlay_apply_skips_when_already_best()
    {
        FakeRegistry(); FakeOverlay();
        _overlay = NativePowerOverlay.BestPerformance;
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "power-overlay-perf" }, null)).Results);
        Assert.True(result.Ok); Assert.False(result.Changed);
    }

    [Fact]
    public async Task Power_overlay_write_failure_reports_failed_result_without_changing_state()
    {
        FakeRegistry();
        NativePowerOverlay.SupportedOverride = () => true;
        NativePowerOverlay.ReadOverride = () => _overlay;
        NativePowerOverlay.WriteOverride = _ => 5;
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "power-overlay-perf" }, null)).Results);
        Assert.False(result.Ok); Assert.False(result.Skipped);
        Assert.Equal(Guid.Empty, _overlay);
    }

    [Fact]
    public async Task Power_overlay_restore_returns_to_recorded_mode()
    {
        FakeRegistry(); FakeOverlay();
        var capture = BackupService.Capture(new[] { "power-overlay-perf" }, null);
        await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "power-overlay-perf" }, null);
        Assert.Equal(NativePowerOverlay.BestPerformance, _overlay);
        var restored = BackupService.RestoreAll(new[] { "power-overlay-perf" }, capture);
        Assert.Empty(restored.Failures);
        Assert.Equal(Guid.Empty, _overlay);
    }

    [Fact]
    public void Power_overlay_validation_rejects_broken_targets()
    {
        Assert.Throws<InvalidOperationException>(() => BackupService.ValidateRecord(
            new BackupRecord { Id = "power-overlay-perf", Kind = "power-overlay", OldAcOverlayGuid = "not-a-guid" }));
        Assert.Throws<InvalidOperationException>(() => BackupService.ValidateRecord(
            new BackupRecord { Id = "sticky-keys-off", Kind = "registry", Hive = "CurrentUser",
                Path = @"Control Panel\Accessibility\StickyKeys", Name = "Flags", ValueKind = "String",
                OldAcOverlayGuid = Guid.Empty.ToString("D") }));
        // filter-keys-off 现为普通 registry 记录：kind 不匹配应拒绝
        Assert.Throws<InvalidOperationException>(() => BackupService.ValidateRecord(
            new BackupRecord { Id = "filter-keys-off", Kind = "filter-keys" }));
    }

    [Fact]
    public async Task Filter_keys_apply_clears_only_hotkey_bits_and_can_restore()
    {
        var original = "126"; // 0x7E：含热键位
        string? current = original;
        RegistryHelper.SnapshotOverride = (_, _, name) => name == "Flags"
            ? new RegistryHelper.Snapshot(true, current, RegistryValueKind.String)
            : new RegistryHelper.Snapshot(false, null, RegistryValueKind.None);
        RegistryHelper.SetOverride = (_, _, name, value, _) => { if (name == "Flags") current = (string)value; };
        var capture = BackupService.Capture(new[] { "filter-keys-off" }, null);
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "filter-keys-off" }, null)).Results);
        Assert.True(result.Ok, result.Message); Assert.True(result.Changed);
        // 只清热键位（0x4|0x8）：126 & ~0xC = 114，其余位原样保留
        Assert.Equal("114", current);
        var record = Assert.Single(BackupService.ReadRecords(capture));
        Assert.Equal("registry", record.Kind);
        Assert.Equal(@"Control Panel\Accessibility\Keyboard Response", record.Path);
        Assert.Equal(original, record.OldValue?.ToString()); // JSON 反序列化为数值类型
        var restored = BackupService.RestoreAll(new[] { "filter-keys-off" }, capture);
        Assert.Empty(restored.Failures);
        Assert.Equal(original, current);
    }

    [Fact]
    public async Task Filter_keys_apply_skips_when_hotkey_already_off()
    {
        RegistryHelper.SnapshotOverride = (_, _, _) => new RegistryHelper.Snapshot(true, "114", RegistryValueKind.String);
        RegistryHelper.SetOverride = (_, _, _, _, _) => throw new UnexpectedWriteException();
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "filter-keys-off" }, null)).Results);
        Assert.True(result.Ok); Assert.False(result.Changed);
    }

    [Fact]
    public async Task Filter_keys_skips_as_unavailable_when_flags_missing()
    {
        RegistryHelper.SnapshotOverride = (_, _, _) => new RegistryHelper.Snapshot(false, null, RegistryValueKind.None);
        RegistryHelper.SetOverride = (_, _, _, _, _) => throw new UnexpectedWriteException();
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "filter-keys-off" }, null)).Results);
        Assert.True(result.Ok); Assert.True(result.Skipped);
    }

    private sealed class UnexpectedWriteException : Exception;
}
