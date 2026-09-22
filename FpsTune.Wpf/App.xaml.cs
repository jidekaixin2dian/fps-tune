using System.IO;
using System.Text;
using System.Windows;
using FpsTune.Wpf.Services;
using System.Windows.Threading;

namespace FpsTune.Wpf;

public partial class App : Application
{
    private AutoProfileService? _autoProfileService;
    private static MetricsSampler? _liveMetrics;
    public static MetricsSampler LiveMetrics => _liveMetrics ??= new(
        TimeSpan.FromSeconds(UiPerformance.LowSpec ? 3 : 1), 120);

    /// <summary>性能会话服务：应用级单例，页面切换不影响运行中的会话。</summary>
    public static PerformanceSessionService SessionService { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // 无头 CLI 模式：命令行以已知动词（-Detect/-Apply/...）开头时不进 GUI，执行完直接退出。
        if (e.Args.Length > 0 && CliHost.IsCliInvocation(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(CliHost.Run(e.Args));
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SettingsService.Load();
        LangService.Load();
        LangService.Apply(LangService.Current);
        UiPerformance.LowSpec = SettingsService.Current.LowSpecMode;
        LegacyMigrations.EnsureRun();
        // 上次异常退出遗留的运行中会话快照：样本足够则转正为一条历史会话
        try
        {
            PerformanceSessionStore.RecoverInterruptedSession();
            PerformanceSessionStore.EnforceRetention();
        }
        catch
        {
            // 恢复失败不阻塞启动
        }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.IsVisibleChanged += (_, _) =>
        {
            if (mainWindow.IsVisible) LiveMetrics.Start(); else _liveMetrics?.Stop();
        };
        mainWindow.Show();
        _autoProfileService = new AutoProfileService();
        _autoProfileService.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _liveMetrics?.Dispose();
        SessionService.Dispose();
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
