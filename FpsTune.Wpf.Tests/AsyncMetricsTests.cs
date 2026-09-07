using System.Diagnostics;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class AsyncMetricsTests
{
    [Fact]
    public Task Slow_sample_does_not_block_dispatcher_or_dispose_and_never_publishes_after_dispose()
        => OnDispatcher(async () =>
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            using var sampler = new MetricsSampler(TimeSpan.FromSeconds(1), 5, () =>
            {
                Interlocked.Increment(ref reads);
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return Sample();
            });
            var callbacks = 0;
            sampler.Sampled += _ => callbacks++;
            var pending = sampler.SampleOnceAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => Assert.False(pending.IsCompleted));
                await sampler.SampleOnceAsync(); // 慢查询期间不会堆积重复任务。
                Assert.Equal(1, reads);
                var watch = Stopwatch.StartNew();
                sampler.Dispose();
                Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250));
            }
            finally { release.Set(); }
            await pending;
            Assert.Equal(0, callbacks);
            Assert.Empty(sampler.Buffer);
        });

    [Fact]
    public Task Async_sample_reads_off_thread_and_publishes_on_owning_dispatcher()
        => OnDispatcher(async () =>
        {
            var ui = Environment.CurrentManagedThreadId;
            var reader = ui;
            using var sampler = new MetricsSampler(null, 2, () =>
            {
                reader = Environment.CurrentManagedThreadId;
                return Sample();
            });
            var callbackThread = -1;
            sampler.Sampled += _ => callbackThread = Environment.CurrentManagedThreadId;
            await sampler.SampleOnceAsync();
            Assert.NotEqual(ui, reader);
            Assert.Equal(ui, callbackThread);
            Assert.Single(sampler.Buffer);
        });

    [Fact]
    public Task Stop_invalidates_pending_sample_but_allows_later_sampling()
        => OnDispatcher(async () =>
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sampler = new MetricsSampler(null, 2, () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return Sample();
            });
            var pending = sampler.SampleOnceAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                sampler.Stop();
            }
            finally { release.Set(); }
            await pending;
            Assert.Empty(sampler.Buffer);
            await sampler.SampleOnceAsync();
            Assert.Single(sampler.Buffer);
        });

    private static MetricSample Sample() => new(DateTime.Now, 10, 20, 30, 100, 200);

    private static Task OnDispatcher(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
