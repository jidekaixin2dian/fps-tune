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
    private NativeAccessibility.FilterKeysState _filterKeys = new(0x7E, 30, 500, 250, 12, 0);

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
        NativeAccessibility.ReadOverride = null;
        NativeAccessibility.WriteOverride = null;
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
        Assert.Throws<InvalidOperationException>(() => BackupService.ValidateRecord(
            new BackupRecord { Id = "filter-keys-off", Kind = "filter-keys", OldFilterKeysFlags = null }));
    }

    [Fact]
    public async Task Filter_keys_apply_clears_only_hotkey_bits_and_can_restore()
    {
        FakeRegistry();
        var writes = new List<NativeAccessibility.FilterKeysState>();
        NativeAccessibility.ReadOverride = () => _filterKeys;
        NativeAccessibility.WriteOverride = state => { writes.Add(state); _filterKeys = state; return 0; };
        var original = _filterKeys;
        var capture = BackupService.Capture(new[] { "filter-keys-off" }, null);
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "filter-keys-off" }, null)).Results);
        Assert.True(result.Ok, result.Message); Assert.True(result.Changed);
        var applied = Assert.Single(writes);
        // 只清热键位（0x4|0x8），其余位与节奏参数原样保留
        Assert.Equal(original.Flags & ~(NativeAccessibility.FkfHotkeyActive | NativeAccessibility.FkfConfirmHotkey), applied.Flags);
        Assert.Equal(original.WaitPeriod, applied.WaitPeriod);
        Assert.Equal(original.RepeatRate, applied.RepeatRate);
        var record = Assert.Single(BackupService.ReadRecords(capture));
        Assert.Equal("filter-keys", record.Kind);
        Assert.Equal(original.Flags, record.OldFilterKeysFlags);
        Assert.Equal(original.BounceMSec, record.OldFilterKeysBounceMSec);
        var restored = BackupService.RestoreAll(new[] { "filter-keys-off" }, capture);
        Assert.Empty(restored.Failures);
        Assert.Equal(original, _filterKeys);
    }

    [Fact]
    public async Task Filter_keys_apply_skips_when_hotkey_already_off()
    {
        _filterKeys = _filterKeys with { Flags = 0x72 };
        NativeAccessibility.ReadOverride = () => _filterKeys;
        NativeAccessibility.WriteOverride = _ => throw new UnexpectedWriteException();
        var result = Assert.Single((await OptimizationEngine.ApplyItemsWithReceiptAsync(new[] { "filter-keys-off" }, null)).Results);
        Assert.True(result.Ok); Assert.False(result.Changed);
    }

    private sealed class UnexpectedWriteException : Exception;
}
