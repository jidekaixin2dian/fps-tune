using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>数字振动服务逻辑测试：全部走内存态假实现。</summary>
[Collection("BackupService serial")]
public class DigitalVibranceTests : IDisposable
{
    private readonly FakeVibranceApi _api = new();
    private readonly string _backupDir;

    public DigitalVibranceTests()
    {
        _backupDir = Path.Combine(Path.GetTempPath(), "fpstune-vib-tests-" + Guid.NewGuid().ToString("N"));
        DigitalVibranceService.ApiOverride = () => _api;
        DigitalVibranceService.BackupDirOverride = _backupDir;
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        DigitalVibranceService.ApiOverride = null;
        DigitalVibranceService.BackupDirOverride = null;
        if (Directory.Exists(_backupDir))
            Directory.Delete(_backupDir, recursive: true);
    }

    [Fact]
    public void GetState_reports_range_and_current()
    {
        _api.Current = 20; _api.Min = 0; _api.Max = 63; _api.Default = 32;
        var state = DigitalVibranceService.GetState();
        Assert.True(state.Supported);
        Assert.Equal(20, state.Current);
        Assert.Equal(0, state.Min);
        Assert.Equal(63, state.Max);
        Assert.False(state.Restorable);
    }

    [Fact]
    public void SetPercent_backs_up_once_and_restores_original()
    {
        _api.Current = 10; _api.Min = 0; _api.Max = 63;

        DigitalVibranceService.SetPercent(75);
        // 75% → 0 + 63*0.75 ≈ 47
        Assert.Equal(47, _api.Current);
        Assert.True(DigitalVibranceService.HasRestorableBackup());

        DigitalVibranceService.SetPercent(80);
        // 二次设置不覆盖首次备份
        Assert.True(DigitalVibranceService.Restore());
        Assert.Equal(10, _api.Current);
        Assert.False(DigitalVibranceService.HasRestorableBackup());
    }

    [Fact]
    public void Restore_without_backup_is_honest_noop()
    {
        Assert.False(DigitalVibranceService.Restore());
    }

    [Fact]
    public void Unsupported_is_reported_honestly()
    {
        _api.SimulateUnsupported = true;
        var state = DigitalVibranceService.GetState();
        Assert.False(state.Supported);
        Assert.NotNull(state.UnsupportedReason);
        Assert.Throws<NvdrsException>(() => DigitalVibranceService.SetPercent(70));
    }

    private sealed class FakeVibranceApi : INvibranceApi
    {
        public int Current { get; set; }
        public int Min { get; set; }
        public int Max { get; set; } = 63;
        public int Default { get; set; }
        public bool SimulateUnsupported { get; set; }
        public string? LastError { get; private set; }

        public bool TryInitialize()
        {
            if (SimulateUnsupported)
                LastError = "NVAPI 不可用";
            return !SimulateUnsupported;
        }

        public bool TryGetInfo(out int current, out int min, out int max, out int defaultValue)
        {
            if (SimulateUnsupported)
            {
                current = min = max = defaultValue = 0;
                return false;
            }
            current = Current; min = Min; max = Max; defaultValue = Default;
            return max > min;
        }

        public void SetLevel(int level) => Current = level;

        public void Dispose() { }
    }
}
