using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 开机还原自启动项（HKCU Run 键）的同步契约：跟两个滤镜功能的开关走，该写就写、该删就删、
/// 幂等、值带完整引号路径；免安装（RootOverride）形态一律不注册。注册表走注入，不写真注册表。
/// 与其它动全局静态（RootOverride/SettingsService.Current）的测试互斥（BackupService serial）。
/// </summary>
[Collection("BackupService serial")]
public sealed class FilterRestoreAutostartTests : IDisposable
{
    private string? _value;
    private int _writes;
    private string? _oldRootOverride;

    public FilterRestoreAutostartTests()
    {
        _oldRootOverride = UserDataPaths.RootOverride;
        UserDataPaths.RootOverride = null; // Sync 对 RootOverride 短路，测试走完整路径
        FilterRestoreAutostart.ValueReaderOverride = () => _value;
        FilterRestoreAutostart.ValueWriterOverride = v => { _value = v; _writes++; };
    }

    public void Dispose()
    {
        FilterRestoreAutostart.ValueReaderOverride = null;
        FilterRestoreAutostart.ValueWriterOverride = null;
        UserDataPaths.RootOverride = _oldRootOverride;
    }

    [Fact]
    public void Autostart_is_written_when_a_filter_feature_is_on_and_removed_when_both_off()
    {
        var settings = SettingsService.Current;
        var oldPerApp = settings.FilterWatcherEnabled;
        var oldFull = settings.FullscreenPresetEnabled;
        try
        {
            settings.FilterWatcherEnabled = true;
            settings.FullscreenPresetEnabled = false;
            _value = null; _writes = 0;
            FilterRestoreAutostart.Sync();
            Assert.Equal(1, _writes);
            Assert.NotNull(_value);
            Assert.EndsWith("-FilterRestore", _value);
            Assert.StartsWith("\"", _value);

            _writes = 0;
            FilterRestoreAutostart.Sync(); // 已是目标值：幂等不动
            Assert.Equal(0, _writes);

            settings.FilterWatcherEnabled = false;
            settings.FullscreenPresetEnabled = false;
            FilterRestoreAutostart.Sync();
            Assert.Equal(1, _writes);
            Assert.Null(_value);
        }
        finally
        {
            settings.FilterWatcherEnabled = oldPerApp;
            settings.FullscreenPresetEnabled = oldFull;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void Portable_or_test_runs_never_register_the_autostart()
    {
        UserDataPaths.RootOverride = Path.Combine(Path.GetTempPath(), "fpstune-portable-" + Guid.NewGuid().ToString("N"));
        try
        {
            SettingsService.Current.FilterWatcherEnabled = true;
            _value = null; _writes = 0;
            FilterRestoreAutostart.Sync();
            Assert.Equal(0, _writes);
            Assert.Null(_value);
        }
        finally
        {
            UserDataPaths.RootOverride = null;
        }
    }

    [Fact]
    public void Should_exist_follows_either_filter_switch()
    {
        var settings = SettingsService.Current;
        var oldPerApp = settings.FilterWatcherEnabled;
        var oldFull = settings.FullscreenPresetEnabled;
        try
        {
            settings.FilterWatcherEnabled = false;
            settings.FullscreenPresetEnabled = false;
            Assert.False(FilterRestoreAutostart.ShouldExist());
            settings.FilterWatcherEnabled = true;
            Assert.True(FilterRestoreAutostart.ShouldExist());
            settings.FilterWatcherEnabled = false;
            settings.FullscreenPresetEnabled = true;
            Assert.True(FilterRestoreAutostart.ShouldExist());
        }
        finally
        {
            settings.FilterWatcherEnabled = oldPerApp;
            settings.FullscreenPresetEnabled = oldFull;
            SettingsService.Save(settings);
        }
    }
}
