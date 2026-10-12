using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CPUx.Core;

namespace CPUx.Services.Stress;

/// <summary>
/// Allocates a large share of free RAM and continuously writes and verifies test patterns
/// (solid bits, alternating bits, address-in-address, pseudo-random) across several threads.
/// </summary>
public sealed unsafe class MemoryStress : StressComponent
{
    private const long ChunkBytes = 64L * 1024 * 1024;

    private readonly int _percent;
    private readonly int _threads;
    private readonly List<(IntPtr Ptr, long Bytes)> _chunks = new();
    private long _allocated, _target, _bytes, _passes;
    private readonly Rate _rate = new();

    public MemoryStress(int percentOfFree, int threads)
    {
        _percent = Math.Clamp(percentOfFree, 10, 90);
        _threads = Math.Clamp(threads, 1, 16);
    }

    public override string Name => "Memory";

    protected override void OnStart()
    {
        var mem = Native.MemoryStatus();
        long free = (long)mem.ullAvailPhys;
        // Leave at least 1.5 GB for Windows so the system doesn't start paging heavily.
        _target = Math.Max(ChunkBytes, Math.Min(free * _percent / 100, free - 1536L * 1024 * 1024));
        _target -= _target % ChunkBytes;
        Status = "Allocating";
        Spawn("CPUx memory stress", ThreadPriority.Normal, Run);
    }

    private void Run()
    {
        var token = Token;
        try
        {
            while (_allocated < _target && !token.IsCancellationRequested)
            {
                IntPtr p = (IntPtr)NativeMemory.Alloc((nuint)ChunkBytes);
                new Span<byte>((void*)p, (int)Math.Min(ChunkBytes, int.MaxValue)).Clear(); // commit every page now
                lock (_chunks) _chunks.Add((p, ChunkBytes));
                Interlocked.Add(ref _allocated, ChunkBytes);
            }
        }
        catch (OutOfMemoryException)
        {
            Say($"stopped allocating at {Fmt.Bytes(_allocated)} (out of memory)");
        }
        if (token.IsCancellationRequested) return;
        Say($"testing {Fmt.Bytes(_allocated)} with {_threads} threads");
        Status = "Running";

        (IntPtr Ptr, long Bytes)[] chunks;
        lock (_chunks) chunks = _chunks.ToArray();
        var workers = new Thread[_threads];
        for (int w = 0; w < _threads; w++)
        {
            int id = w;
            workers[w] = new Thread(() => { Native.DisableThreadThrottling(); Worker(id, chunks); }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"CPUx memory worker {id}" };
            workers[w].Start();
        }
        foreach (var t in workers) t.Join();
    }

    private void Worker(int id, (IntPtr Ptr, long Bytes)[] chunks)
    {
        var token = Token;
        try
        {
            for (int pass = 0; !token.IsCancellationRequested; pass++)
            {
                for (int c = id; c < chunks.Length && !token.IsCancellationRequested; c += _threads)
                {
                    var span = new Span<ulong>((void*)chunks[c].Ptr, (int)(chunks[c].Bytes / 8));
                    ulong seed = (ulong)pass * 0x9E3779B97F4A7C15 ^ (ulong)c * 0xD1B54A32D192ED03;
                    long bad = TestChunk(span, pass % 4, seed, (ulong)chunks[c].Ptr);
                    if (bad > 0)
                        ReportError($"{bad} corrupted words in block {c} (pattern {PatternName(pass % 4)}) — memory is unstable");
                    Interlocked.Add(ref _bytes, chunks[c].Bytes * 2);
                }
                if (id == 0) Interlocked.Increment(ref _passes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex.Message);
        }
    }

    private static string PatternName(int p) => p switch { 0 => "0x55", 1 => "0xAA", 2 => "address", _ => "random" };

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long TestChunk(Span<ulong> span, int pattern, ulong seed, ulong baseAddress)
    {
        switch (pattern)
        {
            case 0:
            case 1:
            {
                ulong v = pattern == 0 ? 0x5555555555555555UL : 0xAAAAAAAAAAAAAAAAUL;
                span.Fill(v);
                if (span.IndexOfAnyExcept(v) < 0) return 0;
                long bad = 0;
                foreach (var x in span) if (x != v) bad++;
                return bad;
            }
            case 2:
            {
                for (int i = 0; i < span.Length; i++) span[i] = (baseAddress + (ulong)i * 8) ^ seed;
                long bad = 0;
                for (int i = 0; i < span.Length; i++) if (span[i] != ((baseAddress + (ulong)i * 8) ^ seed)) bad++;
                return bad;
            }
            default:
            {
                ulong x = seed | 1;
                for (int i = 0; i < span.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; span[i] = x; }
                x = seed | 1;
                long bad = 0;
                for (int i = 0; i < span.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; if (span[i] != x) bad++; }
                return bad;
            }
        }
    }

    public override StressReading Read()
    {
        double gbs = _rate.Per(Interlocked.Read(ref _bytes)) / 1e9;
        long allocated = Interlocked.Read(ref _allocated);
        if (Status == "Allocating")
            return new StressReading(Fmt.Bytes(allocated), $"Allocating {Fmt.Bytes(allocated)} of {Fmt.Bytes(_target)}…", Status, Errors, 0);
        return new StressReading($"{gbs:0.0} GB/s", $"{Fmt.Bytes(allocated)} under test · pass {Interlocked.Read(ref _passes) + 1}", Status, Errors, gbs);
    }

    public string AverageScore() =>
        $"{Interlocked.Read(ref _bytes) / Math.Max(1, Elapsed.TotalSeconds) / 1e9:0.0} GB/s over {Fmt.Bytes(Interlocked.Read(ref _allocated))}, {Interlocked.Read(ref _passes)} full passes";

    protected override void OnStopped()
    {
        lock (_chunks)
        {
            foreach (var c in _chunks) NativeMemory.Free((void*)c.Ptr);
            _chunks.Clear();
        }
    }
}
