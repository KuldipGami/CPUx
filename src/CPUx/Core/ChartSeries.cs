namespace CPUx.Core;

/// <summary>Fixed-capacity ring buffer of samples that charts render. Always mutated on the UI thread.</summary>
public sealed class ChartSeries
{
    private double[] _buffer;
    private int _start;
    private int _count;

    public ChartSeries(int capacity = 60)
    {
        _buffer = new double[Math.Max(2, capacity)];
    }

    public event EventHandler? Updated;

    public int Capacity => _buffer.Length;
    public int Count => _count;
    public double Latest => _count == 0 ? 0 : this[_count - 1];

    /// <summary>Oldest-first indexer.</summary>
    public double this[int index] => _buffer[(_start + index) % _buffer.Length];

    public void Push(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = value;
            _count++;
        }
        else
        {
            _buffer[_start] = value;
            _start = (_start + 1) % _buffer.Length;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public double Max()
    {
        double m = 0;
        for (int i = 0; i < _count; i++) m = Math.Max(m, this[i]);
        return m;
    }

    public double Average()
    {
        if (_count == 0) return 0;
        double s = 0;
        for (int i = 0; i < _count; i++) s += this[i];
        return s / _count;
    }

    public void Resize(int capacity)
    {
        capacity = Math.Max(2, capacity);
        if (capacity == _buffer.Length) return;
        int keep = Math.Min(_count, capacity);
        var next = new double[capacity];
        for (int i = 0; i < keep; i++) next[i] = this[_count - keep + i];
        _buffer = next;
        _start = 0;
        _count = keep;
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
