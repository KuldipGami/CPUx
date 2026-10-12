using System.Diagnostics;
using CPUx.Core;

namespace CPUx.Services.Stress;

/// <summary>Live numbers a stress component reports every tick.</summary>
public sealed record StressReading(string Score, string Detail, string Status, long Errors, double ScoreValue);

/// <summary>Base class for a background load generator (CPU, GPU, memory, disk).</summary>
public abstract class StressComponent : IDisposable
{
    private readonly List<Thread> _threads = new();
    private readonly Stopwatch _clock = new();
    private long _errors;
    private int _stopped;

    protected CancellationTokenSource Cts { get; private set; } = new();
    protected CancellationToken Token => Cts.Token;

    public abstract string Name { get; }
    public volatile string Status = "Idle";
    public string? FirstError { get; private set; }
    public long Errors => Interlocked.Read(ref _errors);
    public bool IsRunning { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Raised from worker threads with human-readable events for the log.</summary>
    public event Action<string>? Message;

    public void Start()
    {
        Cts = new CancellationTokenSource();
        _stopped = 0;
        IsRunning = true;
        _clock.Restart();
        OnStart();
    }

    protected abstract void OnStart();

    /// <summary>Called on the UI thread every tick.</summary>
    public abstract StressReading Read();

    protected void Spawn(string name, ThreadPriority priority, Action body)
    {
        var t = new Thread(() =>
        {
            Native.DisableThreadThrottling();
            try { body(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error($"Stress {Name}", ex);
                Fail($"{Name} worker crashed: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = name,
            Priority = priority,
        };
        lock (_threads) _threads.Add(t);
        t.Start();
    }

    protected void ReportError(string message)
    {
        if (Interlocked.Increment(ref _errors) == 1)
        {
            FirstError = message;
            Message?.Invoke($"{Name}: {message}");
        }
    }

    /// <summary>Counts as an error and stops this component.</summary>
    protected void Fail(string message)
    {
        ReportError(message);
        Status = "Failed";
        Cts.Cancel();
    }

    protected void Say(string message) => Message?.Invoke($"{Name}: {message}");

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        Cts.Cancel();
        Thread[] threads;
        lock (_threads) { threads = _threads.ToArray(); _threads.Clear(); }
        foreach (var t in threads) t.Join(8000);
        _clock.Stop();
        try { OnStopped(); } catch (Exception ex) { Log.Error($"Stress {Name} cleanup", ex); }
        IsRunning = false;
        if (Status is not "Failed") Status = "Stopped";
    }

    protected virtual void OnStopped() { }

    public void Dispose() => Stop();

    /// <summary>Rate helper: returns per-second delta of a monotonically increasing counter.</summary>
    protected sealed class Rate
    {
        private long _last;
        private long _lastTicks = Stopwatch.GetTimestamp();

        public double Per(long total)
        {
            long now = Stopwatch.GetTimestamp();
            double secs = (now - _lastTicks) / (double)Stopwatch.Frequency;
            double r = secs > 0 ? (total - _last) / secs : 0;
            _last = total;
            _lastTicks = now;
            return Math.Max(0, r);
        }
    }
}
