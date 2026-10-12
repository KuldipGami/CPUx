using System.Collections;
using System.Diagnostics;
using CPUx.Core;

namespace CPUx.Services;

/// <summary>
/// Reads every counter/instance of a performance counter category in one call and computes
/// rates between consecutive reads. Much cheaper than one PerformanceCounter per instance.
/// </summary>
public sealed class PerfCategory
{
    private readonly string _name;
    private PerformanceCounterCategory? _category;
    private Dictionary<string, Dictionary<string, CounterSample>> _prev = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, CounterSample>> _cur = new(StringComparer.OrdinalIgnoreCase);
    private bool _failed;

    public PerfCategory(string name) => _name = name;

    public bool Available => !_failed;

    public bool Read()
    {
        if (_failed) return false;
        try
        {
            _category ??= new PerformanceCounterCategory(_name);
            var data = _category.ReadCategory();
            var next = new Dictionary<string, Dictionary<string, CounterSample>>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in data)
            {
                var counter = (string)entry.Key;
                var instances = (InstanceDataCollection)entry.Value!;
                var dict = new Dictionary<string, CounterSample>(instances.Count, StringComparer.OrdinalIgnoreCase);
                foreach (InstanceData d in instances.Values)
                    dict[d.InstanceName] = d.Sample;
                next[counter] = dict;
            }
            _prev = _cur;
            _cur = next;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Perf:{_name}", ex);
            _failed = true;
            return false;
        }
    }

    public IEnumerable<string> Instances(string counter) =>
        _cur.TryGetValue(counter, out var d) ? d.Keys : Enumerable.Empty<string>();

    /// <summary>Calculated (cooked) value between the last two reads.</summary>
    public double Value(string counter, string instance)
    {
        if (!_cur.TryGetValue(counter, out var cur) || !cur.TryGetValue(instance, out var c)) return double.NaN;
        if (!_prev.TryGetValue(counter, out var prev) || !prev.TryGetValue(instance, out var p))
        {
            // Raw counters (e.g. bytes) don't need a previous sample.
            return c.CounterType is PerformanceCounterType.NumberOfItems32 or PerformanceCounterType.NumberOfItems64
                ? c.RawValue : double.NaN;
        }
        try { return CounterSample.Calculate(p, c); }
        catch { return double.NaN; }
    }

    public long Raw(string counter, string instance) =>
        _cur.TryGetValue(counter, out var cur) && cur.TryGetValue(instance, out var c) ? c.RawValue : 0;

    /// <summary>For single-instance categories.</summary>
    public long Raw(string counter) =>
        _cur.TryGetValue(counter, out var cur) && cur.Count > 0 ? cur.Values.First().RawValue : 0;

    public double Value(string counter)
    {
        if (!_cur.TryGetValue(counter, out var cur) || cur.Count == 0) return double.NaN;
        return Value(counter, cur.Keys.First());
    }
}
