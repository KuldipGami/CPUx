using System.Runtime.InteropServices;
using System.Text;
using CPUx.Core;

namespace CPUx.Services.Stress;

/// <summary>
/// Saturates a GPU with a Direct3D 11 compute shader (dense FP32 multiply-add chains) and,
/// optionally, large buffer copies that hammer video memory. Results are read back periodically
/// and compared with the first run: any difference means the GPU computed something wrong.
/// A driver reset (TDR) during the test is reported as a failure.
/// </summary>
public sealed unsafe class GpuStress : StressComponent
{
    private const int GroupSize = 256;
    private const int Groups = 1024;
    private const int Threads = GroupSize * Groups;
    private const int Iterations = 512;
    private const long FlopsPerDispatch = (long)Threads * Iterations * 8 * 4 * 4 * 2;
    private const double TargetBatchMs = 45;

    private const string Shader = """
        RWStructuredBuffer<float4> Result : register(u0);

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            float s = (float)(id.x & 1023) * 0.0009765625;
            float4 x0 = float4(s, s + 0.11, s + 0.23, s + 0.37);
            float4 x1 = x0 + 0.5;
            float4 x2 = x0 + 1.0;
            float4 x3 = x0 + 1.5;
            const float4 b = float4(0.99991, 1.00007, 0.99997, 1.00003);
            const float4 c = float4(0.00013, -0.00011, 0.00007, -0.00005);
            [loop] for (uint i = 0; i < ITERATIONS; i++)
            {
                [unroll] for (uint j = 0; j < 8; j++)
                {
                    x0 = mad(x0, b, c);
                    x1 = mad(x1, b, c);
                    x2 = mad(x2, b, c);
                    x3 = mad(x3, b, c);
                }
            }
            Result[id.x] = x0 + x1 + x2 + x3;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryDesc
    {
        public int Query;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3dcompiler_47.dll")]
    private static extern int D3DCompile(byte* source, nuint length, byte* sourceName, IntPtr defines, IntPtr include,
        byte* entryPoint, byte* target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    private readonly GpuInfo _gpu;
    private readonly bool _memory;
    private long _flops, _copied, _checks;
    private readonly Rate _flopRate = new(), _copyRate = new();
    private ulong _vramBytes;

    public GpuStress(GpuInfo gpu, bool includeMemory)
    {
        _gpu = gpu;
        _memory = includeMemory;
    }

    public override string Name => "GPU";

    protected override void OnStart()
    {
        Status = "Starting";
        Spawn("CPUx GPU stress", ThreadPriority.AboveNormal, Run);
    }

    private static IntPtr* Vt(IntPtr com) => *(IntPtr**)com;

    private static void Release(ref IntPtr com)
    {
        if (com != IntPtr.Zero) Marshal.Release(com);
        com = IntPtr.Zero;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    private IntPtr FindAdapter()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Check(Native.CreateDXGIFactory1(ref iid, out IntPtr factory), "CreateDXGIFactory1");
        try
        {
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vt(factory)[12];
            for (uint i = 0; i < 16; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0) break;
                int size = Marshal.SizeOf<Native.DXGI_ADAPTER_DESC1>();
                IntPtr mem = Marshal.AllocHGlobal(size);
                try
                {
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vt(adapter)[10];
                    if (getDesc1(adapter, mem) >= 0)
                    {
                        var d = Marshal.PtrToStructure<Native.DXGI_ADAPTER_DESC1>(mem);
                        if ($"luid_0x{d.LuidHigh:X8}_0x{d.LuidLow:X8}".Equals(_gpu.LuidKey, StringComparison.OrdinalIgnoreCase))
                            return adapter;
                    }
                }
                finally { Marshal.FreeHGlobal(mem); }
                Marshal.Release(adapter);
            }
        }
        finally { Marshal.Release(factory); }
        throw new InvalidOperationException("graphics adapter not found");
    }

    private static byte[] Compile()
    {
        byte[] src = Encoding.ASCII.GetBytes($"#define ITERATIONS {Iterations}\n{Shader}");
        byte[] name = Encoding.ASCII.GetBytes("cpux_stress.hlsl\0");
        byte[] entry = Encoding.ASCII.GetBytes("main\0");
        byte[] target = Encoding.ASCII.GetBytes("cs_5_0\0");
        IntPtr code, errors;
        int hr;
        fixed (byte* s = src, n = name, e = entry, t = target)
            hr = D3DCompile(s, (nuint)src.Length, n, IntPtr.Zero, IntPtr.Zero, e, t, 1u << 15, 0, out code, out errors);
        try
        {
            if (hr < 0)
            {
                string msg = errors != IntPtr.Zero
                    ? Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Vt(errors)[3])(errors)) ?? ""
                    : "";
                throw new InvalidOperationException($"shader compile failed (0x{hr:X8}) {msg}");
            }
            IntPtr ptr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Vt(code)[3])(code);
            nuint len = ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)Vt(code)[4])(code);
            var bytes = new byte[(int)len];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Release(ref code);
            Release(ref errors);
        }
    }

    private void Run()
    {
        IntPtr adapter = 0, device = 0, ctx = 0, cs = 0, output = 0, uav = 0, staging = 0, query = 0, query2 = 0, bigA = 0, bigB = 0;
        var token = Token;
        try
        {
            adapter = FindAdapter();
            int level = 0xB000; // D3D_FEATURE_LEVEL_11_0
            Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, &level, 1, 7, out device, out _, out ctx), "D3D11CreateDevice");

            var dev = Vt(device);
            var createBuffer = (delegate* unmanaged[Stdcall]<IntPtr, BufferDesc*, IntPtr, IntPtr*, int>)dev[3];
            var createUav = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)dev[8];
            var createCs = (delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)dev[18];
            var createQuery = (delegate* unmanaged[Stdcall]<IntPtr, QueryDesc*, IntPtr*, int>)dev[24];
            var removedReason = (delegate* unmanaged[Stdcall]<IntPtr, int>)dev[39];

            var c = Vt(ctx);
            var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSubresource*, int>)c[14];
            var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)c[15];
            var end = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)c[28];
            var getData = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, uint, int>)c[29];
            var dispatch = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)c[41];
            var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)c[47];
            var csSetUavs = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)c[68];
            var csSetShader = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)c[69];
            var clearState = (delegate* unmanaged[Stdcall]<IntPtr, void>)c[110];

            byte[] bytecode = Compile();
            fixed (byte* bc = bytecode) Check(createCs(device, bc, (nuint)bytecode.Length, IntPtr.Zero, &cs), "CreateComputeShader");

            const uint OutputBytes = Threads * 16;
            var outDesc = new BufferDesc { ByteWidth = OutputBytes, Usage = 0, BindFlags = 0x80, MiscFlags = 0x40, StructureByteStride = 16 };
            Check(createBuffer(device, &outDesc, IntPtr.Zero, &output), "CreateBuffer(output)");
            Check(createUav(device, output, IntPtr.Zero, &uav), "CreateUnorderedAccessView");
            var stDesc = new BufferDesc { ByteWidth = OutputBytes, Usage = 3, CpuAccessFlags = 0x20000, MiscFlags = 0x40, StructureByteStride = 16 };
            Check(createBuffer(device, &stDesc, IntPtr.Zero, &staging), "CreateBuffer(staging)");
            var qd = new QueryDesc { Query = 0 };
            Check(createQuery(device, &qd, &query), "CreateQuery");
            Check(createQuery(device, &qd, &query2), "CreateQuery");

            if (_memory)
            {
                ulong dedicated = _gpu.DedicatedBytes;
                ulong size = _gpu.IsIntegrated || dedicated < 512UL * 1024 * 1024
                    ? 256UL * 1024 * 1024
                    : Math.Clamp(dedicated / 5, 128UL * 1024 * 1024, 1024UL * 1024 * 1024);
                size -= size % 16;
                var bd = new BufferDesc { ByteWidth = (uint)size, Usage = 0, BindFlags = 0x80, MiscFlags = 0x40, StructureByteStride = 16 };
                if (createBuffer(device, &bd, IntPtr.Zero, &bigA) >= 0 && createBuffer(device, &bd, IntPtr.Zero, &bigB) >= 0)
                    _vramBytes = size;
                else
                {
                    Release(ref bigA);
                    Release(ref bigB);
                    Say("couldn't allocate video memory buffers; running compute only");
                }
            }

            csSetShader(ctx, cs, null, 0);
            IntPtr uavLocal = uav;
            csSetUavs(ctx, 0, 1, &uavLocal, null);

            Status = "Running";
            Say($"running on {_gpu.ShortName}" + (_vramBytes > 0 ? $" with {Fmt.Bytes(_vramBytes * 2)} of buffers" : ""));

            byte[]? reference = null;
            var current = new byte[OutputBytes];
            var queries = new[] { query, query2 };
            var batchSizes = new int[2];
            int perBatch = 1, slot = 0;
            bool previousPending = false;
            var verifyClock = System.Diagnostics.Stopwatch.StartNew();
            var batchClock = System.Diagnostics.Stopwatch.StartNew();

            // Double-buffered: batch N+1 is queued before waiting on batch N, so the GPU never idles
            // while this thread sleeps.
            while (!token.IsCancellationRequested)
            {
                for (int k = 0; k < perBatch; k++) dispatch(ctx, Groups, 1, 1);
                if (_vramBytes > 0)
                {
                    copyResource(ctx, bigB, bigA);
                    copyResource(ctx, bigA, bigB);
                }
                end(ctx, queries[slot]);
                batchSizes[slot] = perBatch;
                int previous = slot ^ 1;
                slot = previous;
                if (!previousPending)
                {
                    previousPending = true;
                    continue;
                }

                int hr, done = 0;
                while ((hr = getData(ctx, queries[previous], &done, 4, 0)) == 1)
                {
                    if (token.IsCancellationRequested) break;
                    Thread.Sleep(1);
                }
                if (hr < 0)
                {
                    int reason = removedReason(device);
                    Fail($"the graphics driver stopped responding (0x{(reason != 0 ? reason : hr):X8}). This usually means the GPU is unstable at its current clocks or temperature.");
                    break;
                }
                if (token.IsCancellationRequested) break;

                double ms = batchClock.Elapsed.TotalMilliseconds;
                batchClock.Restart();
                int finished = batchSizes[previous];
                Interlocked.Add(ref _flops, FlopsPerDispatch * finished);
                if (_vramBytes > 0) Interlocked.Add(ref _copied, (long)_vramBytes * 4); // two copies, read + write each

                // Keep each batch around 45 ms: busy enough to saturate, far from the 2 s driver timeout.
                if (ms > 0) perBatch = (int)Math.Clamp(finished * TargetBatchMs / ms, 1, 4096);

                if (verifyClock.Elapsed.TotalSeconds >= 2)
                {
                    verifyClock.Restart();
                    copyResource(ctx, staging, output);
                    MappedSubresource mapped;
                    if (map(ctx, staging, 0, 1, 0, &mapped) >= 0)
                    {
                        new ReadOnlySpan<byte>(mapped.Data, (int)OutputBytes).CopyTo(current);
                        unmap(ctx, staging, 0);
                        if (reference == null) reference = (byte[])current.Clone();
                        else if (!current.AsSpan().SequenceEqual(reference))
                        {
                            int bad = 0;
                            for (int i = 0; i < current.Length; i += 4)
                                if (BitConverter.ToUInt32(current, i) != BitConverter.ToUInt32(reference, i)) bad++;
                            ReportError($"{bad} values computed differently from the first run — the GPU is producing errors");
                        }
                        Interlocked.Increment(ref _checks);
                    }
                }
            }
            clearState(ctx);
        }
        catch (DllNotFoundException ex)
        {
            Fail($"Direct3D components missing: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex.Message);
        }
        finally
        {
            Release(ref bigB);
            Release(ref bigA);
            Release(ref query2);
            Release(ref query);
            Release(ref staging);
            Release(ref uav);
            Release(ref output);
            Release(ref cs);
            Release(ref ctx);
            Release(ref device);
            Release(ref adapter);
        }
    }

    public override StressReading Read()
    {
        double tflops = _flopRate.Per(Interlocked.Read(ref _flops)) / 1e12;
        double gbs = _copyRate.Per(Interlocked.Read(ref _copied)) / 1e9;
        string score = tflops >= 1 ? $"{tflops:0.00} TFLOPS" : $"{tflops * 1000:0} GFLOPS";
        string detail = $"{_gpu.ShortName} · FP32 compute"
                        + (_vramBytes > 0 ? $" · VRAM {gbs:0} GB/s" : "")
                        + $" · {Interlocked.Read(ref _checks)} checks";
        return new StressReading(score, detail, Status, Errors, tflops * 1000);
    }

    public string AverageScore()
    {
        double secs = Math.Max(1, Elapsed.TotalSeconds);
        double tflops = Interlocked.Read(ref _flops) / secs / 1e12;
        string s = $"{tflops:0.00} TFLOPS FP32 on {_gpu.ShortName}";
        if (_vramBytes > 0) s += $", VRAM copy {Interlocked.Read(ref _copied) / secs / 1e9:0} GB/s";
        return s;
    }
}
