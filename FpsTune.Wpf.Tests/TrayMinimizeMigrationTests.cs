using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 0.4.0 最小化行为迁移专项：旧默认"最小化进托盘"一次性回归任务栏最小化；
/// 迁移只发生一次，此后用户手动开启不被翻转。全部走 RootOverride 临时目录。
/// </summary>
[Collection("BackupService serial")]
public sealed class TrayMinimizeMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fps-tune-tests", Guid.NewGuid().ToString("N"));

    public TrayMinimizeMigrationTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        UserDataPaths.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
    }

    [Fact]
    public void Legacy_tray_default_migrates_once_and_persists()
    {
        // 旧版设置：最小化进托盘（旧默认 true），无迁移标记
        var settingsFile = Path.Combine(_root, "settings.json");
        File.WriteAllText(settingsFile, """{"MinimizeToTray": true, "CheckUpdatesOnStartup": true}""");

        SettingsService.Load();
        Assert.False(SettingsService.Current.MinimizeToTray);
        Assert.Equal(SettingsService.TrayMinimizeMigrationVersion, SettingsService.Current.TrayMinimizeMigration);
        // 迁移立即持久化，避免下次启动重复翻转
        Assert.Contains("TrayMinimizeMigration", File.ReadAllText(settingsFile));

        // 用户此后手动开启：不被再次翻转
        SettingsService.Current.MinimizeToTray = true;
        SettingsService.Save(SettingsService.Current);
        SettingsService.Load();
        Assert.True(SettingsService.Current.MinimizeToTray);
    }

    [Fact]
    public void Fresh_install_defaults_to_taskbar_minimize_and_self_heals_legacy_true()
    {
        SettingsService.Load(); // 无设置文件：默认即任务栏最小化，尚未打迁移标记
        Assert.False(SettingsService.Current.MinimizeToTray);
        Assert.Equal("", SettingsService.Current.TrayMinimizeMigration);
        // 无标记的旧默认 true（含被回写的场景）：Load 即自愈翻转
        var settingsFile = Path.Combine(_root, "settings.json");
        File.WriteAllText(settingsFile, """{"MinimizeToTray": true}""");
        SettingsService.Load();
        Assert.False(SettingsService.Current.MinimizeToTray);
        Assert.Equal(SettingsService.TrayMinimizeMigrationVersion, SettingsService.Current.TrayMinimizeMigration);
    }

    [Fact]
    public void User_opted_out_stays_out()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"MinimizeToTray": false}""");
        SettingsService.Load();
        Assert.False(SettingsService.Current.MinimizeToTray);
        Assert.Equal(SettingsService.TrayMinimizeMigrationVersion, SettingsService.Current.TrayMinimizeMigration);
    }
}
