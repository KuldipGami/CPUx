using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CPUx.Core;

namespace CPUx.Services.Stress;

/// <summary>
/// Writes a temporary file with verifiable data, then cycles sequential write, sequential
/// read-and-verify and random 4K read/write phases. The OS cache is bypassed so the numbers
/// reflect the drive. The file is deleted automatically when the test stops or the app exits.
/// </summary>
public sealed unsafe class DiskStress : StressComponent
{
    private const int SeqBlock = 1024 * 1024;
    private const int RandBlock = 4096;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private readonly string _drive;
    private readonly long _size;
    private string _path = "";
    private SafeFileHandle? _handle;
    private bool _unbuffered;
    private long _written, _read, _randomOps;
    private readonly Rate _writeRate = new(), _readRate = new(), _iopsRate = new();
    private volatile string _phase = "Preparing";
    private double _bestSeqWrite, _bestSeqRead, _bestIops;

    public DiskStress(string driveRoot, long sizeBytes)
    {
        _drive = driveRoot;
        _size = sizeBytes - sizeBytes % SeqBlock;
    }

    public override string Name => "Disk";

    public static string TestPath(string driveRoot)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return driveRoot.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), "CPUx-stress.tmp")
            : Path.Combine(driveRoot, "CPUx-stress.tmp");
    }

    protected override void OnStart()
    {
        Status = "Preparing";
        Spawn("CPUx disk stress", ThreadPriority.Normal, Run);
    }

    private SafeFileHandle Open(string path)
    {
        var options = FileOptions.WriteThrough | FileOptions.DeleteOnClose;
        try
        {
            _unbuffered = true;
            return File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, options | NoBuffering, _size);
        }
        catch (ArgumentException)
        {
            _unbuffered = false;
            return File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, options, _size);
        }
    }

    private void Run()
    {
        var token = Token;
        var drive = new DriveInfo(_drive);
        long reserve = Math.Max(2L * 1024 * 1024 * 1024, drive.TotalSize / 20);
        if (drive.AvailableFreeSpace - _size < reserve)
        {
            Fail($"not enough free space on {_drive} (needs {Fmt.Bytes(_size + reserve)})");
            return;
        }

        _path = TestPath(_drive);
        try { _handle = Open(_path); }
        catch (UnauthorizedAccessException)
        {
            string dir = Path.Combine(_drive, "CPUx-stress");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "CPUx-stress.tmp");
            _handle = Open(_path);
        }
        Say($"using {Fmt.Bytes(_size)} test file at {_path}{(_unbuffered ? "" : " (OS cache could not be bypassed)")}");
        Status = "Running";

        byte* buffer = (byte*)NativeMemory.AlignedAlloc(SeqBlock, 4096);
        try
        {
            for (ulong pass = 1; !token.IsCancellationRequested; pass++)
            {
                // 1. Sequential write
                _phase = "Sequential write";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (long off = 0; off < _size && !token.IsCancellationRequested; off += SeqBlock)
                {
                    Fill(new Span<ulong>(buffer, SeqBlock / 8), off, pass);
                    RandomAccess.Write(_handle, new ReadOnlySpan<byte>(buffer, SeqBlock), off);
                    Interlocked.Add(ref _written, SeqBlock);
                }
                if (token.IsCancellationRequested) break;
                _bestSeqWrite = Math.Max(_bestSeqWrite, _size / sw.Elapsed.TotalSeconds);

                // 2. Sequential read + verify
                _phase = "Sequential read";
                sw.Restart();
                for (long off = 0; off < _size && !token.IsCancellationRequested; off += SeqBlock)
                {
                    int n = RandomAccess.Read(_handle, new Span<byte>(buffer, SeqBlock), off);
                    Interlocked.Add(ref _read, n);
                    long bad = Verify(new ReadOnlySpan<ulong>(buffer, n / 8), off, pass);
                    if (bad > 0) ReportError($"{bad} words read back wrong at offset {Fmt.Bytes(off)} — data corruption");
                }
                if (token.IsCancellationRequested) break;
                _bestSeqRead = Math.Max(_bestSeqRead, _size / sw.Elapsed.TotalSeconds);

                // 3. Random 4K, 4 threads, 70% read / 30% write, 20 seconds
                _phase = "Random 4K";
                long opsBefore = Interlocked.Read(ref _randomOps);
                sw.Restart();
                var workers = Enumerable.Range(0, 4).Select(i =>
                {
                    var t = new Thread(() => { Native.DisableThreadThrottling(); RandomWorker(i, pass, TimeSpan.FromSeconds(20)); }) { IsBackground = true, Name = $"CPUx disk random {i}" };
                    t.Start();
                    return t;
                }).ToList();
                foreach (var t in workers) t.Join();
                if (sw.Elapsed.TotalSeconds > 1)
                    _bestIops = Math.Max(_bestIops, (Interlocked.Read(ref _randomOps) - opsBefore) / sw.Elapsed.TotalSeconds);
                Say($"pass {pass} done · write {Fmt.Rate(_bestSeqWrite)} · read {Fmt.Rate(_bestSeqRead)} · {_bestIops:N0} IOPS");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private void RandomWorker(int id, ulong pass, TimeSpan duration)
    {
        var token = Token;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(RandBlock, 4096);
        try
        {
            var rng = new Random(unchecked((int)pass * 31 + id));
            long blocks = _size / RandBlock;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < duration && !token.IsCancellationRequested)
            {
                long off = rng.NextInt64(blocks) * RandBlock;
                if (rng.Next(10) < 3)
                {
                    Fill(new Span<ulong>(buf, RandBlock / 8), off, pass);
                    RandomAccess.Write(_handle!, new ReadOnlySpan<byte>(buf, RandBlock), off);
                    Interlocked.Add(ref _written, RandBlock);
                }
                else
                {
                    int n = RandomAccess.Read(_handle!, new Span<byte>(buf, RandBlock), off);
                    Interlocked.Add(ref _read, n);
                    long bad = Verify(new ReadOnlySpan<ulong>(buf, n / 8), off, pass);
                    if (bad > 0) ReportError($"{bad} words read back wrong at offset {Fmt.Bytes(off)} (random read)");
                }
                Interlocked.Increment(ref _randomOps);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex.Message);
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    // Stateless pattern: any word can be regenerated from its file position and the pass number.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Pattern(long wordIndex, ulong pass)
    {
        ulong z = (ulong)wordIndex + pass * 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Fill(Span<ulong> words, long byteOffset, ulong pass)
    {
        long first = byteOffset / 8;
        for (int i = 0; i < words.Length; i++) words[i] = Pattern(first + i, pass);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Verify(ReadOnlySpan<ulong> words, long byteOffset, ulong pass)
    {
        long first = byteOffset / 8, bad = 0;
        for (int i = 0; i < words.Length; i++) if (words[i] != Pattern(first + i, pass)) bad++;
        return bad;
    }

    public override StressReading Read()
    {
        double w = _writeRate.Per(Interlocked.Read(ref _written));
        double r = _readRate.Per(Interlocked.Read(ref _read));
        double iops = _iopsRate.Per(Interlocked.Read(ref _randomOps));
        string phase = _phase;
        string score = phase switch
        {
            "Sequential write" => Fmt.Rate(w),
            "Sequential read" => Fmt.Rate(r),
            "Random 4K" => $"{iops:N0} IOPS",
            _ => Fmt.Dash,
        };
        string detail = $"{phase} · {Fmt.Bytes(_size)} file · {Fmt.Bytes(Interlocked.Read(ref _written))} written";
        return new StressReading(score, detail, Status, Errors, (w + r) / 1e6);
    }

    public string AverageScore() =>
        $"best sequential write {Fmt.Rate(_bestSeqWrite)}, read {Fmt.Rate(_bestSeqRead)}, random 4K {_bestIops:N0} IOPS · {Fmt.Bytes(Interlocked.Read(ref _written))} written";

    protected override void OnStopped()
    {
        _handle?.Dispose(); // DeleteOnClose removes the file
        _handle = null;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            var dir = Path.GetDirectoryName(_path);
            if (dir != null && dir.EndsWith("CPUx-stress", StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }
}
