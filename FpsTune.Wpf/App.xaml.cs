using System.IO;
using System.Text;
using System.Windows;
using FpsTune.Wpf.Core;
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

    static App()
    {
        SessionService.StateChanged += UpdateLiveMetrics;
        SessionService.Sampled += sample =>
        {
            if (_liveMetrics?.HasSubscribers == true && PreviewWindowVisible)
                _liveMetrics.PublishExternal(sample, SessionService.CurrentUnavailableReasons);
        };
    }

    private static bool PreviewWindowVisible => Current?.MainWindow is { IsVisible: true } window
        && window.WindowState != WindowState.Minimized;

    internal static void UpdateLiveMetrics()
    {
        if (_liveMetrics is null) return;
        if (PreviewWindowVisible && _liveMetrics.HasSubscribers && !SessionService.IsRunning)
            _liveMetrics.Start();
        else
            _liveMetrics.Stop();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // In-process render verification never starts tray, updates, profiles or migrations.
        if (UserDataPaths.RootOverride is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            return;
        }
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
        // 主题在启动画面显示前就位，保证 splash 第一帧就是正确配色
        ThemeManager.Initialize();
        ThemeManager.SetMode(
            string.IsNullOrWhiteSpace(SettingsService.Current.ThemeMode) ? "dark" : SettingsService.Current.ThemeMode);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // 启动画面：先于主窗显示，让"整个软件完全加载出来之前"有可见反馈。
        // 主窗的构建（XAML 解析 + 首页实例化）在 splash 首帧渲染之后进行（ApplicationIdle 排在 Render 之后）。
        var splash = new Views.SplashWindow();
        splash.Show();

        // 预热硬件信息：WMI 查询较慢，后台先取好并落缓存，首页加载时直接命中
        _ = Task.Run(() =>
        {
            try { HardwareInfoService.Get(); }
            catch
            {
                // 预热失败不阻塞启动；页面自会按原路径重试
            }
        });

        base.OnStartup(e);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            splash.SetPhase(48, Str.T("Str.SplashPhaseRestore"));
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

            splash.SetPhase(80, Str.T("Str.SplashPhaseBuild"));
            GameContextService.RestoreSavedSelection();
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.IsVisibleChanged += (_, _) =>
            {
                UpdateLiveMetrics();
            };
            mainWindow.StateChanged += (_, _) => UpdateLiveMetrics();
            // 首帧渲染 ≠ 首页数据就绪（控制台概览首启要跑后台检测、经典概览要等 WMI）。
            // splash 等到「首帧 + 首页数据就绪信号」双条件齐备才放行，超时兜底防启动卡死。
            var firstFrame = new TaskCompletionSource();
            var homeReady = new TaskCompletionSource();
            mainWindow.ContentRendered += (_, _) => firstFrame.TrySetResult();
            mainWindow.HomeDataReady += (_, _) => homeReady.TrySetResult();
            mainWindow.Closed += (_, _) =>
            {
                try { splash.Close(); } catch { /* 已关闭则忽略 */ }
            };
            mainWindow.Show();
            splash.SetPhase(92, Str.T("Str.SplashPhaseData"));
            _ = CloseSplashWhenReadyAsync(splash, firstFrame.Task, homeReady.Task);
            _autoProfileService = new AutoProfileService();
            _autoProfileService.Start();

        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _liveMetrics?.Dispose();
        SessionService.Dispose();
        _autoProfileService?.Dispose();
        TrayService.Dispose();
        base.OnExit(e);
    }

    /// <summary>splash 数据阶段最长等待：超时放行，首页在后台继续加载，绝不卡死启动。</summary>
    private static readonly TimeSpan SplashDataTimeout = TimeSpan.FromSeconds(15);

    private static async Task CloseSplashWhenReadyAsync(
        Views.SplashWindow splash, Task firstFrame, Task homeReady)
    {
        var ready = Task.WhenAll(firstFrame, homeReady);
        _ = await Task.WhenAny(ready, Task.Delay(SplashDataTimeout));
        try { splash.CloseWithFade(); } catch { /* 已关闭则忽略 */ }
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
            var dir = Path.Combine(UserDataPaths.Root, "logs");
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
