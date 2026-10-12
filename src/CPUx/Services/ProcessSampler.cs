using System.Runtime.InteropServices;
using CPUx.Core;

namespace CPUx.Services;

/// <summary>Enumerates every process with a single NtQuerySystemInformation call (no per-process handles needed).</summary>
public sealed unsafe class ProcessSampler
{
    private sealed class Prev
    {
        public long CpuTime;
        public long IoBytes;
        public long CreateTime;
    }

    private Dictionary<int, Prev> _prev = new();
    private long _lastTicks;
    private IntPtr _buffer = IntPtr.Zero;
    private int _bufferSize = 512 * 1024;

    public List<ProcSample> Sample()
    {
        var list = new List<ProcSample>(400);
        long now = Environment.TickCount64;
        double elapsedMs = _lastTicks == 0 ? 0 : now - _lastTicks;
        _lastTicks = now;
        int cpus = Environment.ProcessorCount;

        if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_bufferSize);
        uint status;
        int needed;
        while ((status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buffer, _bufferSize, out needed)) == Native.STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(_bufferSize * 2, needed + 64 * 1024);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }
        if (status != 0)
        {
            Log.Write($"NtQuerySystemInformation failed: 0x{status:X8}");
            return list;
        }

        var next = new Dictionary<int, Prev>(_prev.Count + 16);
        byte* p = (byte*)_buffer;
        while (true)
        {
            var info = (Native.SYSTEM_PROCESS_INFORMATION*)p;
            int pid = (int)info->UniqueProcessId;
            string name = info->ImageName.Buffer == IntPtr.Zero
                ? (pid == 0 ? "System Idle Process" : "System")
                : new string((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2);
            long cpuTime = info->UserTime + info->KernelTime;
            long io = info->ReadTransferCount + info->WriteTransferCount;

            var s = new ProcSample
            {
                Pid = pid,
                ParentPid = (int)info->InheritedFromUniqueProcessId,
                Name = name,
                Threads = (int)info->NumberOfThreads,
                Handles = (int)info->HandleCount,
                Session = (int)info->SessionId,
                Priority = info->BasePriority,
                PrivateWs = info->WorkingSetPrivateSize,
                WorkingSet = (long)(ulong)info->WorkingSetSize,
                Commit = (long)(ulong)info->PagefileUsage,
                Started = info->CreateTime > 0 ? DateTime.FromFileTime(info->CreateTime) : DateTime.MinValue,
            };

            if (elapsedMs > 0 && _prev.TryGetValue(pid, out var prev) && prev.CreateTime == info->CreateTime)
            {
                // CPU times are in 100 ns units.
                double cpuMs = (cpuTime - prev.CpuTime) / 10_000.0;
                s.Cpu = Math.Clamp(cpuMs / elapsedMs / cpus * 100.0, 0, 100);
                s.IoBps = Math.Max(0, (io - prev.IoBytes) * 1000.0 / elapsedMs);
            }
            next[pid] = new Prev { CpuTime = cpuTime, IoBytes = io, CreateTime = info->CreateTime };
            if (pid != 0) list.Add(s);

            if (info->NextEntryOffset == 0) break;
            p += info->NextEntryOffset;
        }
        _prev = next;
        return list;
    }

    public void Reset()
    {
        _prev.Clear();
        _lastTicks = 0;
    }
}
