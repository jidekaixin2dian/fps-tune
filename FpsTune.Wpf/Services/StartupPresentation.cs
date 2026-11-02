using System.Windows;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

/// <summary>The startup presentation belongs to the shell, never to a cached page's lifetime.</summary>
internal static class StartupPresentation
{
    internal static async Task CompleteAsync(MainWindow main, SplashWindow splash, TimeSpan? timeout = null, Action? reveal = null)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopped = new CancellationTokenSource();
        var revealed = false;
        var mainClosed = false;
        void RevealOnce()
        {
            if (revealed) return;
            revealed = true;
            reveal?.Invoke();
        }
        void Frame(object? s, EventArgs e) => frame.TrySetResult();
        void Ready(object? s, EventArgs e) => ready.TrySetResult();
        void Closed(object? s, EventArgs e) => stopped.Cancel();
        void MainClosed(object? s, EventArgs e) { mainClosed = true; stopped.Cancel(); }
        main.ContentRendered += Frame;
        main.HomeDataReady += Ready;
        main.StartupNavigationRequested += Ready;
        main.Closed += MainClosed;
        splash.Closed += Closed;
        try
        {
            var deadline = Task.Delay(timeout ?? TimeSpan.FromSeconds(15), stopped.Token);
            await Task.WhenAny(Task.WhenAll(frame.Task, ready.Task), deadline);
            // 加载窗被提前关掉（例如点了「进入工作台」）时也要保证主界面显形，否则会一直停在透明的窗口上。
            if (!mainClosed) RevealOnce();
            if (mainClosed || stopped.IsCancellationRequested) return;
            splash.SetPhase(100, Str.T("Str.StartupReady"));
            splash.CloseWithFade();
        }
        finally
        {
            stopped.Cancel();
            main.ContentRendered -= Frame;
            main.HomeDataReady -= Ready;
            main.StartupNavigationRequested -= Ready;
            main.Closed -= MainClosed;
            splash.Closed -= Closed;
        }
    }
}
