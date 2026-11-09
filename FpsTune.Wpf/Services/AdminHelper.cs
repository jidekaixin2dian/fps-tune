using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace FpsTune.Wpf.Services;

public static class AdminHelper
{
    /// <summary>测试注入：把"是否管理员"固定下来，免得用例结果取决于跑测试的账户。</summary>
    internal static Func<bool>? IsAdministratorOverride { get; set; }

    public static bool IsAdministrator()
    {
        if (IsAdministratorOverride is not null)
            return IsAdministratorOverride();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static void RestartAsAdministrator()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
        }
        catch
        {
            DialogService.Warning("FPS 帧律", "无法以管理员身份重启，可能已被取消。");
        }
    }
}
