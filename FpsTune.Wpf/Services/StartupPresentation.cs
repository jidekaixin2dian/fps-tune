using System.Windows;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

/// <summary>The startup presentation belongs to the shell, never to a cached page's lifetime.</summary>
internal static class StartupPresentation
{
    internal static async Task CompleteAsync(MainWindow main, SplashWindow splash, TimeSpan? timeout = null)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopped = new CancellationTokenSource();
        void Frame(object? s, EventArgs e) => frame.TrySetResult();
        void Ready(object? s, EventArgs e) => ready.TrySetResult();
        void Closed(object? s, EventArgs e) => stopped.Cancel();
        main.ContentRendered += Frame;
        main.HomeDataReady += Ready;
        main.StartupNavigationRequested += Ready;
        main.Closed += Closed;
        splash.Closed += Closed;
        try
        {
            var deadline = Task.Delay(timeout ?? TimeSpan.FromSeconds(15), stopped.Token);
            await Task.WhenAny(Task.WhenAll(frame.Task, ready.Task), deadline);
            if (stopped.IsCancellationRequested) return;
            splash.SetPhase(100, Str.T("Str.StartupReady"));
            splash.CloseWithFade();
        }
        finally
        {
            stopped.Cancel();
            main.ContentRendered -= Frame;
            main.HomeDataReady -= Ready;
            main.StartupNavigationRequested -= Ready;
            main.Closed -= Closed;
            splash.Closed -= Closed;
        }
    }
}
