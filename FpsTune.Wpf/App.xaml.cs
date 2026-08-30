using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using FpsTune.Wpf.Services;
using System.Windows.Threading;

namespace FpsTune.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SettingsService.Load();
        UiPerformance.LowSpec = SettingsService.Current.LowSpecMode;
        LegacyMigrations.EnsureRun();
        PreferDiscreteGpuForSelf();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TrayService.Dispose();
        base.OnExit(e);
    }


    /// <summary>
    /// 混合显卡笔记本上让 WPF 渲染走独立显卡（写入系统 per-app GPU 偏好），
    /// 界面动画/滚动更顺滑；只写本程序自己的条目，卸载残留无副作用。
    /// </summary>
    private static void PreferDiscreteGpuForSelf()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            var current = key.GetValue(exe) as string;
            if (current == "GpuPreference=2;")
                return;
            key.SetValue(exe, "GpuPreference=2;", RegistryValueKind.String);
        }
        catch
        {
            // 非关键优化，失败不影响启动。
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        DialogService.Warning(
            "FPS 帧律",
            "程序发生错误：\n\n" + e.Exception.Message + "\n\n详细信息已写入日志。");
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogException(ex);
    }

    private static void LogException(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FpsTune", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "error.log");
            var content = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]")
                .AppendLine(ex.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(path, content, Encoding.UTF8);
        }
        catch
        {
            // ignore logging errors
        }
    }
}
