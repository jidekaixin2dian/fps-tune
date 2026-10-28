namespace FpsTune.Wpf.Services;

public sealed record LiveFpsSnapshot(int Samples, double WindowSeconds, double Fps, double P1Low);

/// <summary>At most five seconds / 32768 frame times; allocated only while the HUD requests data.</summary>
internal sealed class LiveFrameWindow(Func<long>? milliseconds = null)
{
    private const int Capacity = 32768;
    private readonly object _gate = new();
    private readonly Func<long> _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    private double[]? _frames;
    private int _start, _count;
    private double _total;
    private long _lastFrame;
    private bool _enabled;

    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled; _frames = null; _start = _count = 0; _total = 0;
        }
    }
    internal void Add(double ms)
    {
        if (!Volatile.Read(ref _enabled) || !double.IsFinite(ms) || ms <= 0) return;
        lock (_gate)
        {
            if (!_enabled) return;
            _frames ??= new double[Capacity];
            if (_count > 0 && _milliseconds() - _lastFrame > 3000) { _start = _count = 0; _total = 0; }
            while (_count > 0 && (_count == Capacity || _total + ms > 5000))
            {
                _total -= _frames[_start]; _start = (_start + 1) % Capacity; _count--;
                if (_count == 0) _total = 0;
            }
            _frames[(_start + _count) % Capacity] = ms; _count++; _total += ms;
            _lastFrame = _milliseconds();
        }
    }
    internal LiveFpsSnapshot? Read()
    {
        double[] values; double total;
        lock (_gate)
        {
            if (!_enabled || _count < 30 || _milliseconds() - _lastFrame > 3000) return null;
            values = new double[_count]; total = _total;
            for (var i = 0; i < _count; i++) values[i] = _frames![(_start + i) % Capacity];
        }
        Array.Sort(values);
        return new(values.Length, total / 1000, values.Length * 1000 / total,
            1000 / values[Math.Min((int)Math.Floor(values.Length * .99), values.Length - 1)]);
    }
}
