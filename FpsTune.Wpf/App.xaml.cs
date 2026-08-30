using System.IO;
using System.Text;
using System.Windows;
using FpsTune.Wpf.Services;
using System.Windows.Threading;

namespace FpsTune.Wpf;

public partial class App : Application
{
    private AutoProfileService? _autoProfileService;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SettingsService.Load();
        UiPerformance.LowSpec = SettingsService.Current.LowSpecMode;
        LegacyMigrations.EnsureRun();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);
        _autoProfileService = new AutoProfileService();
        _autoProfileService.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _autoProfileService?.Dispose();
        TrayService.Dispose();
        base.OnExit(e);
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
