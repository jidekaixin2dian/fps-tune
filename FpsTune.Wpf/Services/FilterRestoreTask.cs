using System.Diagnostics;
using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 「重启电脑后自动还原滤镜」的登录计划任务（对齐 1U 的开机恢复）。
///
/// 全屏方案或独立程序滤镜把屏幕改掉后，如果程序被直接杀死、或用户重启电脑后一直没打开
/// 程序，未还原的凭证就一直留着。程序启动时本来就会还原；这个任务把同样的还原提前到
/// **登录时**：计划任务登录触发 <c>FpsTune.exe -FilterRestore</c>，只消费 pending 凭证、
/// 不启动界面、不套任何新滤镜。
///
/// 注册跟随功能开关：两个滤镜功能都关着就没有这个任务（默认安装不会创建任何计划任务）。
/// 免安装/测试运行不注册。任务创建/删除失败不影响本程序运行，下次启动重试。
/// </summary>
internal static class FilterRestoreTask
{
    internal const string TaskName = "FpsTuneFilterRestore";

    /// <summary>测试注入：跳过真实 schtasks。</summary>
    internal static Func<string, bool>? TaskExistsOverride { get; set; }
    internal static Func<string, int>? RunnerOverride { get; set; }

    /// <summary>任务是否应当存在：任一滤镜功能开着就应当存在。</summary>
    internal static bool ShouldExist()
        => SettingsService.Current.FilterWatcherEnabled || SettingsService.Current.FullscreenPresetEnabled;

    /// <summary>程序启动时同步一次：该建就建、该删就删，幂等。</summary>
    public static void Sync()
    {
        // 测试/免安装形态不注册计划任务。
        if (UserDataPaths.RootOverride is not null) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

        var want = ShouldExist();
        var has = TaskExistsOverride is not null
            ? TaskExistsOverride(TaskName)
            : RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;
        if (want == has) return;

        // 建删失败只是没同步成：还原兜底仍在程序启动路径里，功能不受影响，下次启动重试。
        var args = want
            ? $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" -FilterRestore\" /SC ONLOGON /RL LIMITED /F"
            : $"/Delete /TN \"{TaskName}\" /F";
        _ = RunSchtasks(args);
    }

    /// <summary>真实执行：schtasks 直调，返回退出码（启动失败返回 -1），不抛异常。</summary>
    private static int RunSchtasks(string arguments)
    {
        if (RunnerOverride is { } runner) return runner(arguments);
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null) return -1;
            process.WaitForExit(15000);
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
