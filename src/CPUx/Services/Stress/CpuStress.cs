using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using CPUx.Core;

namespace CPUx.Services.Stress;

/// <summary>
/// Loads every selected logical processor with floating-point (FMA/SIMD) and integer work.
/// Each kernel is deterministic, so every result is checked against a reference value computed
/// up front; a mismatch means the CPU produced a wrong answer under load (unstable overclock,
/// undervolt or overheating).
/// </summary>
public sealed class CpuStress : StressComponent
{
    public enum Mode { Mixed, Fma, Integer }

    private const int FmaLoops = 131_072;
    private const int IntWords = 32 * 1024;   // 256 KB per thread, stays in L2
    private const int IntRounds = 4;
    private static readonly int Width = Avx512F.IsSupported ? 8 : Fma.IsSupported && Avx.IsSupported ? 4 : Vector<double>.Count;
    private static readonly long FlopsPerFmaCall = (long)FmaLoops * 8 * Width * 2;
    private const long BytesPerIntCall = (long)IntWords * 8 * IntRounds;
    private static readonly ulong[] Seeds = { 1, 7, 31, 127, 8191, 131071, 524287, 2147483647 };

    private readonly int _threads;
    private readonly Mode _mode;
    private double[] _fmaReference = Array.Empty<double>();
    private ulong[] _intReference = Array.Empty<ulong>();
    private long _flops, _intBytes;
    private readonly Rate _flopRate = new(), _intRate = new();

    public CpuStress(int threads, Mode mode)
    {
        _threads = Math.Clamp(threads, 1, Environment.ProcessorCount);
        _mode = mode;
    }

    public override string Name => "CPU";

    public static string InstructionPath =>
        Avx512F.IsSupported ? "AVX-512 FMA" : Fma.IsSupported && Avx.IsSupported ? "AVX2 FMA" : Vector.IsHardwareAccelerated ? "SIMD" : "scalar";

    protected override void OnStart()
    {
        Status = "Preparing";
        // Reference results, computed once before load is applied.
        _fmaReference = Seeds.Select(s => FmaKernel(s)).ToArray();
        var buffer = IntBuffer();
        _intReference = Seeds.Select(s => IntKernel(buffer, s)).ToArray();

        for (int i = 0; i < _threads; i++)
        {
            int index = i;
            // Below-normal priority lets Windows services (networking, WMI, drivers) preempt the workers, so the
            // system and our own monitoring stay responsive. Each worker opts out of EcoQoS (see Spawn), so
            // Windows still runs it on every core at full clocks — measured throughput is identical.
            Spawn($"CPUx CPU stress {i}", ThreadPriority.BelowNormal, () => Worker(index));
        }
        Status = "Running";
        Say($"started {_threads} thread{(_threads == 1 ? "" : "s")} · {ModeName(_mode)} · {InstructionPath}");
    }

    public static string ModeName(Mode m) => m switch
    {
        Mode.Fma => "floating point (FMA)",
        Mode.Integer => "integer",
        _ => "mixed FP + integer",
    };

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Worker(int index)
    {
        var buffer = IntBuffer();
        var token = Token;
        int round = index;
        while (!token.IsCancellationRequested)
        {
            int s = round % Seeds.Length;
            bool doFma = _mode == Mode.Fma || (_mode == Mode.Mixed && (round & 1) == 0);
            if (doFma)
            {
                double r = FmaKernel(Seeds[s]);
                if (BitConverter.DoubleToInt64Bits(r) != BitConverter.DoubleToInt64Bits(_fmaReference[s]))
                    ReportError($"floating-point result mismatch on thread {index} (got {r:R}, expected {_fmaReference[s]:R})");
                Interlocked.Add(ref _flops, FlopsPerFmaCall);
            }
            else
            {
                ulong r = IntKernel(buffer, Seeds[s]);
                if (r != _intReference[s])
                    ReportError($"integer result mismatch on thread {index} (got {r:X16}, expected {_intReference[s]:X16})");
                Interlocked.Add(ref _intBytes, BytesPerIntCall);
            }
            round++;
        }
    }

    public override StressReading Read()
    {
        double gflops = _flopRate.Per(Interlocked.Read(ref _flops)) / 1e9;
        double intGbs = _intRate.Per(Interlocked.Read(ref _intBytes)) / 1e9;
        string score = _mode switch
        {
            Mode.Integer => $"{intGbs:0.0} GB/s",
            _ => $"{gflops:0.0} GFLOPS",
        };
        string detail = _mode switch
        {
            Mode.Mixed => $"{_threads} threads · FP64 {gflops:0.0} GFLOPS · integer {intGbs:0.0} GB/s",
            Mode.Fma => $"{_threads} threads · FP64 {InstructionPath}",
            _ => $"{_threads} threads · hashing in L2 cache",
        };
        return new StressReading(score, detail, Status, Errors, _mode == Mode.Integer ? intGbs : gflops);
    }

    public string AverageScore()
    {
        double secs = Math.Max(1, Elapsed.TotalSeconds);
        double gflops = Interlocked.Read(ref _flops) / secs / 1e9;
        double gbs = Interlocked.Read(ref _intBytes) / secs / 1e9;
        return _mode switch
        {
            Mode.Fma => $"{gflops:0.0} GFLOPS (FP64)",
            Mode.Integer => $"{gbs:0.0} GB/s integer",
            _ => $"{gflops:0.0} GFLOPS (FP64) + {gbs:0.0} GB/s integer",
        };
    }

    // ------------------------------------------------------------------ kernels
    // Each instruction set gets its own small method so the JIT keeps all accumulators in registers.
    // AggressiveOptimization skips tier-0 JIT so the hot loops run fully optimized from the first call.
    private static double FmaKernel(ulong seed)
    {
        double s = (seed % 1000) * 1e-3 + 0.1;
        if (Avx512F.IsSupported) return Fma512(s);
        if (Fma.IsSupported && Avx.IsSupported) return Fma256(s);
        return FmaVector(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static double Fma512(double s)
    {
        var b = Vector512.Create(0.999999, 1.000001, 0.9999995, 1.0000005, 0.9999993, 1.0000007, 0.9999991, 1.0000009);
        var c = Vector512.Create(1e-7, -1e-7, 2e-7, -2e-7, 3e-7, -3e-7, 4e-7, -4e-7);
        Vector512<double> x0 = Vector512.Create(s), x1 = Vector512.Create(s + 0.1), x2 = Vector512.Create(s + 0.2), x3 = Vector512.Create(s + 0.3);
        Vector512<double> x4 = Vector512.Create(s + 0.4), x5 = Vector512.Create(s + 0.5), x6 = Vector512.Create(s + 0.6), x7 = Vector512.Create(s + 0.7);
        for (int i = 0; i < FmaLoops; i++)
        {
            x0 = Avx512F.FusedMultiplyAdd(x0, b, c); x1 = Avx512F.FusedMultiplyAdd(x1, b, c);
            x2 = Avx512F.FusedMultiplyAdd(x2, b, c); x3 = Avx512F.FusedMultiplyAdd(x3, b, c);
            x4 = Avx512F.FusedMultiplyAdd(x4, b, c); x5 = Avx512F.FusedMultiplyAdd(x5, b, c);
            x6 = Avx512F.FusedMultiplyAdd(x6, b, c); x7 = Avx512F.FusedMultiplyAdd(x7, b, c);
        }
        return Vector512.Sum(((x0 + x1) + (x2 + x3)) + ((x4 + x5) + (x6 + x7)));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static double Fma256(double s)
    {
        var b = Vector256.Create(0.999999, 1.000001, 0.9999995, 1.0000005);
        var c = Vector256.Create(1e-7, -1e-7, 2e-7, -2e-7);
        Vector256<double> x0 = Vector256.Create(s), x1 = Vector256.Create(s + 0.1), x2 = Vector256.Create(s + 0.2), x3 = Vector256.Create(s + 0.3);
        Vector256<double> x4 = Vector256.Create(s + 0.4), x5 = Vector256.Create(s + 0.5), x6 = Vector256.Create(s + 0.6), x7 = Vector256.Create(s + 0.7);
        for (int i = 0; i < FmaLoops; i++)
        {
            x0 = Fma.MultiplyAdd(x0, b, c); x1 = Fma.MultiplyAdd(x1, b, c);
            x2 = Fma.MultiplyAdd(x2, b, c); x3 = Fma.MultiplyAdd(x3, b, c);
            x4 = Fma.MultiplyAdd(x4, b, c); x5 = Fma.MultiplyAdd(x5, b, c);
            x6 = Fma.MultiplyAdd(x6, b, c); x7 = Fma.MultiplyAdd(x7, b, c);
        }
        return Vector256.Sum(((x0 + x1) + (x2 + x3)) + ((x4 + x5) + (x6 + x7)));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static double FmaVector(double s)
    {
        var b = new Vector<double>(0.999999);
        var c = new Vector<double>(1e-7);
        Vector<double> x0 = new(s), x1 = new(s + 0.1), x2 = new(s + 0.2), x3 = new(s + 0.3);
        Vector<double> x4 = new(s + 0.4), x5 = new(s + 0.5), x6 = new(s + 0.6), x7 = new(s + 0.7);
        for (int i = 0; i < FmaLoops; i++)
        {
            x0 = x0 * b + c; x1 = x1 * b + c; x2 = x2 * b + c; x3 = x3 * b + c;
            x4 = x4 * b + c; x5 = x5 * b + c; x6 = x6 * b + c; x7 = x7 * b + c;
        }
        return Vector.Sum(((x0 + x1) + (x2 + x3)) + ((x4 + x5) + (x6 + x7)));
    }
    private static ulong[] IntBuffer()
    {
        var buf = new ulong[IntWords];
        ulong x = 0x9E3779B97F4A7C15;
        for (int i = 0; i < buf.Length; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            buf[i] = x;
        }
        return buf;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong IntKernel(ulong[] buf, ulong seed)
    {
        ulong h = seed * 0xBF58476D1CE4E5B9;
        for (int r = 0; r < IntRounds; r++)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                ulong v = buf[i] ^ h;
                v *= 0x9E3779B97F4A7C15;
                v ^= v >> 29;
                h = BitOperations.RotateLeft(h ^ v, 27) * 5 + 0x52DCE729;
            }
        }
        return h;
    }
}
