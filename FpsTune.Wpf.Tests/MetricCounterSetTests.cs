using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class MetricCounterSetTests
{
    private sealed class Counter : IDisposable
    {
        public int Reads;
        public bool Disposed;
        public bool Fail;
        public double Read() { if (Fail) throw new InvalidOperationException(); return ++Reads * 10; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Refresh_preserves_existing_baseline_and_primes_only_added_instances()
    {
        var opened = new Dictionary<string, Counter>();
        using var set = new MetricCounterSet<Counter>(name => opened[name] = new(), c => c.Read());
        set.Refresh(["a"]);
        Assert.Empty(set.Read(true));
        Assert.Equal(20, Assert.Single(set.Read(true)).Value);
        set.Refresh(["a", "b"]);
        Assert.Equal(("a", 30d), Assert.Single(set.Read(true)));
        Assert.Equal(2, set.Read(true).Count);
        Assert.False(opened["a"].Disposed);
        set.Refresh(["b"]);
        Assert.True(opened["a"].Disposed);
        Assert.Equal(("b", 30d), Assert.Single(set.Read(true)));
    }

    [Fact]
    public void Failed_refresh_releases_new_handles_and_preserves_working_set()
    {
        var opened = new Dictionary<string, Counter>();
        using var set = new MetricCounterSet<Counter>(name => name == "bad" ? throw new InvalidOperationException() : opened[name] = new(), c => c.Read());
        set.Refresh(["a"]);
        set.Read(true);
        Assert.Throws<InvalidOperationException>(() => set.Refresh(["a", "b", "bad"]));
        Assert.True(opened["b"].Disposed);
        Assert.False(opened["a"].Disposed);
        Assert.Equal(("a", 20d), Assert.Single(set.Read(true)));
    }

    [Fact]
    public void Raw_values_are_available_immediately_and_actual_failure_is_not_replaced_with_old_value()
    {
        var counter = new Counter();
        using var set = new MetricCounterSet<Counter>(_ => counter, c => c.Read());
        set.Refresh(["a"]);
        Assert.Equal(10, Assert.Single(set.Read(false)).Value);
        counter.Fail = true;
        Assert.Empty(set.Read(false));
        counter.Fail = false;
        Assert.Equal(20, Assert.Single(set.Read(false)).Value);
    }
}
