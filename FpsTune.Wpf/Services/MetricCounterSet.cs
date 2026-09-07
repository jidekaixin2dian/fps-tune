namespace FpsTune.Wpf.Services;

/// <summary>按实例身份增量更新；存活实例保留利用率计算基线。</summary>
internal sealed class MetricCounterSet<T>(Func<string, T> create, Func<T, double> read) : IDisposable where T : IDisposable
{
    private sealed class Entry(T counter)
    {
        public T Counter { get; } = counter;
        public bool Primed { get; set; }
    }
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public int Count => _entries.Count;

    public void Refresh(IEnumerable<string> names)
    {
        var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var added = new List<Entry>();
        try
        {
            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                if (!_entries.TryGetValue(name, out var entry))
                {
                    entry = new Entry(create(name));
                    added.Add(entry);
                }
                next.Add(name, entry);
            }
        }
        catch
        {
            foreach (var entry in added) Release(entry);
            throw;
        }
        foreach (var pair in _entries)
            if (!next.ContainsKey(pair.Key)) Release(pair.Value);
        _entries = next;
    }

    public IReadOnlyList<(string Instance, double Value)> Read(bool needsBaseline)
    {
        var values = new List<(string, double)>();
        foreach (var pair in _entries)
        {
            try
            {
                var value = read(pair.Value.Counter);
                if ((!needsBaseline || pair.Value.Primed) && double.IsFinite(value)) values.Add((pair.Key, value));
                pair.Value.Primed = true;
            }
            catch { pair.Value.Primed = false; }
        }
        return values;
    }
    private static void Release(Entry entry) { try { entry.Counter.Dispose(); } catch { } }
    public void Dispose()
    {
        foreach (var entry in _entries.Values) Release(entry);
        _entries.Clear();
    }
}
