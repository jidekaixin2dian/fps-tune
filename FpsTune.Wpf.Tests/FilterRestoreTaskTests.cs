using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 开机还原计划任务的同步契约：跟两个滤镜功能的开关走，该建建、该删删、幂等；
/// 免安装（RootOverride）形态一律不注册。真实 schtasks 用注入替换，测试不写系统计划任务。
/// </summary>
public sealed class FilterRestoreTaskTests : IDisposable
{
    private readonly List<string> _executed = new();
    private bool _taskExists;
    private string? _oldRootOverride;

    public FilterRestoreTaskTests()
    {
        _oldRootOverride = UserDataPaths.RootOverride;
        UserDataPaths.RootOverride = null; // Sync 对 RootOverride 短路，测试走完整路径
        FilterRestoreTask.TaskExistsOverride = _ => _taskExists;
        FilterRestoreTask.RunnerOverride = args => { _executed.Add(args); return 0; };
    }

    public void Dispose()
    {
        FilterRestoreTask.TaskExistsOverride = null;
        FilterRestoreTask.RunnerOverride = null;
        UserDataPaths.RootOverride = _oldRootOverride;
    }

    [Fact]
    public void Task_is_created_when_a_filter_feature_is_on_and_removed_when_both_off()
    {
        var settings = SettingsService.Current;
        var oldPerApp = settings.FilterWatcherEnabled;
        var oldFull = settings.FullscreenPresetEnabled;
        try
        {
            settings.FilterWatcherEnabled = true;
            settings.FullscreenPresetEnabled = false;
            _taskExists = false;
            FilterRestoreTask.Sync();
            Assert.Single(_executed);
            Assert.Contains("/Create", _executed[0]);
            Assert.Contains(FilterRestoreTask.TaskName, _executed[0]);
            Assert.Contains("-FilterRestore", _executed[0]);

            _taskExists = true;
            _executed.Clear();
            FilterRestoreTask.Sync(); // 已存在且该存在：幂等不动
            Assert.Empty(_executed);

            settings.FilterWatcherEnabled = false;
            settings.FullscreenPresetEnabled = false;
            FilterRestoreTask.Sync();
            Assert.Single(_executed);
            Assert.Contains("/Delete", _executed[0]);
        }
        finally
        {
            settings.FilterWatcherEnabled = oldPerApp;
            settings.FullscreenPresetEnabled = oldFull;
            SettingsService.Save(settings);
        }
    }

    [Fact]
    public void Portable_or_test_runs_never_register_the_task()
    {
        UserDataPaths.RootOverride = Path.Combine(Path.GetTempPath(), "fpstune-portable-" + Guid.NewGuid().ToString("N"));
        try
        {
            SettingsService.Current.FilterWatcherEnabled = true;
            _taskExists = false;
            FilterRestoreTask.Sync();
            Assert.Empty(_executed);
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
            Assert.False(FilterRestoreTask.ShouldExist());
            settings.FilterWatcherEnabled = true;
            Assert.True(FilterRestoreTask.ShouldExist());
            settings.FilterWatcherEnabled = false;
            settings.FullscreenPresetEnabled = true;
            Assert.True(FilterRestoreTask.ShouldExist());
        }
        finally
        {
            settings.FilterWatcherEnabled = oldPerApp;
            settings.FullscreenPresetEnabled = oldFull;
            SettingsService.Save(settings);
        }
    }
}
