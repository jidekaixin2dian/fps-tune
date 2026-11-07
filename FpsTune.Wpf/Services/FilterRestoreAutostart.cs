using System.IO;
using FpsTune.Wpf.Core;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 「重启电脑后自动还原滤镜」的登录自启动项（对齐 参考实现的开机恢复）。
///
/// 全屏方案或独立程序滤镜把屏幕改掉后，如果程序被直接杀死、或用户重启电脑后一直没打开
/// 程序，未还原的凭证就一直留着。程序启动时本来就会还原；这个自启动项把同样的还原提前到
/// **登录时**：<c>FpsTune.exe -FilterRestore</c> 只消费 pending 凭证，不启动界面、不套任何
/// 新滤镜，跑完立即退出。
///
/// 为什么是 HKCU Run 键而不是计划任务：登录触发的计划任务在标准权限下注册会被系统拒绝
/// （schtasks 与 Task Scheduler COM 都试过，0x80070005），而 Run 键免管理员、与既有的
/// 开机自启动（BackupService.SetAutostart）同一机制。参考实现用的是计划任务，用户可见效果一致。
///
/// 注册跟随功能开关：两个滤镜功能都关着就没有这个自启动项（默认安装零注册）。
/// 免安装/测试运行不注册。写入失败不影响本程序运行，下次启动重试。
/// </summary>
internal static class FilterRestoreAutostart
{
    internal const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "FpsTuneFilterRestore";

    /// <summary>注册表读写的测试注入（RegistryHelper 本身也有同名注入，这里留作断言出口）。</summary>
    internal static Func<string?>? ValueReaderOverride { get; set; }
    internal static Action<string?>? ValueWriterOverride { get; set; }

    /// <summary>自启动项是否应当存在：任一滤镜功能开着就应当存在。</summary>
    internal static bool ShouldExist()
        => SettingsService.Current.FilterWatcherEnabled || SettingsService.Current.FullscreenPresetEnabled;

    internal static string DesiredValue(string executablePath) => "\"" + executablePath + "\" -FilterRestore";

    /// <summary>程序启动时同步一次：该写就写、该删就删，幂等。</summary>
    public static void Sync()
    {
        // 测试/免安装形态不注册自启动。
        if (UserDataPaths.RootOverride is not null) return;
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        if (!File.Exists(exe)) return;

        var want = ShouldExist();
        try
        {
            var current = ValueReaderOverride is not null ? ValueReaderOverride()
                : RegistryHelper.ReadValue(RegistryHive.CurrentUser, RunKeyPath, RunValueName) as string;
            if (want)
            {
                var desired = DesiredValue(exe);
                if (current != desired)
                {
                    if (ValueWriterOverride is not null) ValueWriterOverride(desired);
                    else RegistryHelper.SetValue(RegistryHive.CurrentUser, RunKeyPath, RunValueName, desired, Microsoft.Win32.RegistryValueKind.String);
                }
            }
            else if (current is not null)
            {
                if (ValueWriterOverride is not null) ValueWriterOverride(null);
                else RegistryHelper.DeleteValue(RegistryHive.CurrentUser, RunKeyPath, RunValueName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 没同步成：还原兜底仍在程序启动路径里，功能不受影响，下次启动重试。
        }
    }
}
