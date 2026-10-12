using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CPUx.Core;

namespace CPUx.Services;

/// <summary>Minimal NVIDIA Management Library binding (nvml.dll ships with the NVIDIA driver).</summary>
public static class Nvml
{
    private const string Dll = "nvml.dll";
    private static bool _initialized;
    private static bool _resolverSet;

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
    }

    [DllImport(Dll)] private static extern int nvmlInit_v2();
    [DllImport(Dll)] private static extern int nvmlShutdown();
    [DllImport(Dll)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Dll)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll)] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport(Dll)] private static extern int nvmlDeviceGetUUID(IntPtr device, byte[] uuid, uint length);
    [DllImport(Dll)] private static extern int nvmlDeviceGetVbiosVersion(IntPtr device, byte[] version, uint length);
    [DllImport(Dll)] private static extern int nvmlSystemGetDriverVersion(byte[] version, uint length);
    [DllImport(Dll)] private static extern int nvmlSystemGetNVMLVersion(byte[] version, uint length);
    [DllImport(Dll)] private static extern int nvmlSystemGetCudaDriverVersion_v2(out int version);
    [DllImport(Dll)] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll)] private static extern int nvmlDeviceGetTemperatureThreshold(IntPtr device, int threshold, out uint temp);
    [DllImport(Dll)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory mem);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
    [DllImport(Dll)] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int pstate);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint gen);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint width);
    [DllImport(Dll)] private static extern int nvmlDeviceGetEncoderUtilization(IntPtr device, out uint util, out uint periodUs);
    [DllImport(Dll)] private static extern int nvmlDeviceGetDecoderUtilization(IntPtr device, out uint util, out uint periodUs);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryBusWidth(IntPtr device, out uint bits);
    [DllImport(Dll)] private static extern int nvmlDeviceGetNumGpuCores(IntPtr device, out uint cores);
    [DllImport(Dll)] private static extern int nvmlDeviceGetArchitecture(IntPtr device, out uint arch);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, out PciInfo pci);

    public static bool Initialize()
    {
        if (_initialized) return true;
        try
        {
            if (!_resolverSet)
            {
                _resolverSet = true;
                NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);
            }
            _initialized = nvmlInit_v2() == 0;
        }
        catch (Exception ex)
        {
            Log.Write($"NVML unavailable: {ex.GetType().Name}: {ex.Message}");
            _initialized = false;
        }
        return _initialized;
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (!name.Equals(Dll, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        if (NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, Dll), out var h)) return h;
        var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", Dll);
        if (NativeLibrary.TryLoad(nvsmi, out h)) return h;
        return IntPtr.Zero;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        try { nvmlShutdown(); } catch { }
        _initialized = false;
    }

    private static T? Try<T>(Func<T?> f)
    {
        try { return f(); }
        catch { return default; }
    }

    private static string Str(Func<byte[], int> call, int size = 96)
    {
        try
        {
            var buf = new byte[size];
            if (call(buf) != 0) return "";
            int n = Array.IndexOf(buf, (byte)0);
            return Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n).Trim();
        }
        catch { return ""; }
    }

    public sealed record Device(IntPtr Handle, string Name, uint PciDeviceId, string BusId);

    public static List<Device> Devices()
    {
        var list = new List<Device>();
        if (!Initialize()) return list;
        try
        {
            if (nvmlDeviceGetCount_v2(out uint count) != 0) return list;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out IntPtr h) != 0) continue;
                string name = Str(b => nvmlDeviceGetName(h, b, (uint)b.Length));
                uint pciId = 0;
                string bus = "";
                try
                {
                    if (nvmlDeviceGetPciInfo_v3(h, out var pci) == 0) { pciId = pci.PciDeviceId; bus = pci.BusId; }
                }
                catch { }
                list.Add(new Device(h, name, pciId, bus));
            }
        }
        catch (Exception ex) { Log.Error("NVML devices", ex); }
        return list;
    }

    public static InfoSection Details(Device d)
    {
        var h = d.Handle;
        var s = new InfoSection("NVIDIA");
        uint u;
        s.AddIf("Architecture", Try(() => nvmlDeviceGetArchitecture(h, out u) == 0 ? ArchName(u) : null));
        s.AddIf("CUDA compute capability", Try(() => nvmlDeviceGetCudaComputeCapability(h, out int ma, out int mi) == 0 ? $"{ma}.{mi}" : null));
        s.AddIf("CUDA cores", Try(() => nvmlDeviceGetNumGpuCores(h, out u) == 0 && u > 0 ? u.ToString("N0") : null));
        s.AddIf("Memory bus width", Try(() => nvmlDeviceGetMemoryBusWidth(h, out u) == 0 && u > 0 ? $"{u}-bit" : null));
        s.AddIf("Total memory (NVML)", Try(() => nvmlDeviceGetMemoryInfo(h, out var m) == 0 ? Fmt.Bytes(m.Total) : null));
        s.AddIf("Max graphics clock", Try(() => nvmlDeviceGetMaxClockInfo(h, 0, out u) == 0 ? Fmt.MHz(u) : null));
        s.AddIf("Max SM clock", Try(() => nvmlDeviceGetMaxClockInfo(h, 1, out u) == 0 ? Fmt.MHz(u) : null));
        s.AddIf("Max memory clock", Try(() => nvmlDeviceGetMaxClockInfo(h, 2, out u) == 0 ? Fmt.MHz(u) : null));
        s.AddIf("Max video clock", Try(() => nvmlDeviceGetMaxClockInfo(h, 3, out u) == 0 ? Fmt.MHz(u) : null));
        s.AddIf("Power limit (enforced)", Try(() => nvmlDeviceGetEnforcedPowerLimit(h, out u) == 0 ? Fmt.Watts(u / 1000.0) : null));
        s.AddIf("Power limit (default)", Try(() => nvmlDeviceGetPowerManagementDefaultLimit(h, out u) == 0 ? Fmt.Watts(u / 1000.0) : null));
        s.AddIf("Power limit range", Try(() => nvmlDeviceGetPowerManagementLimitConstraints(h, out uint mn, out uint mx) == 0
            ? $"{Fmt.Watts(mn / 1000.0)} – {Fmt.Watts(mx / 1000.0)}" : null));
        s.AddIf("Slowdown temperature", Try(() => nvmlDeviceGetTemperatureThreshold(h, 1, out u) == 0 && u > 0 ? Fmt.Temp(u) : null));
        s.AddIf("Shutdown temperature", Try(() => nvmlDeviceGetTemperatureThreshold(h, 0, out u) == 0 && u > 0 ? Fmt.Temp(u) : null));
        s.AddIf("Max PCIe link", Try(() =>
            nvmlDeviceGetMaxPcieLinkGeneration(h, out uint g) == 0 && nvmlDeviceGetMaxPcieLinkWidth(h, out uint w) == 0 ? $"PCIe {g}.0 x{w}" : null));
        s.AddIf("VBIOS version", Str(b => nvmlDeviceGetVbiosVersion(h, b, (uint)b.Length), 32));
        s.AddIf("UUID", Str(b => nvmlDeviceGetUUID(h, b, (uint)b.Length), 96));
        s.AddIf("PCI bus ID", d.BusId);
        s.AddIf("Driver version", Str(b => nvmlSystemGetDriverVersion(b, (uint)b.Length), 80));
        s.AddIf("CUDA driver version", Try(() => nvmlSystemGetCudaDriverVersion_v2(out int v) == 0 ? $"{v / 1000}.{v % 1000 / 10}" : null));
        s.AddIf("NVML version", Str(b => nvmlSystemGetNVMLVersion(b, (uint)b.Length), 80));
        return s;
    }

    private static string? ArchName(uint a) => a switch
    {
        2 => "Kepler", 3 => "Maxwell", 4 => "Pascal", 5 => "Volta", 6 => "Turing",
        7 => "Ampere", 8 => "Ada Lovelace", 9 => "Hopper", 10 => "Blackwell", _ => null,
    };

    public sealed class Live
    {
        public double Temp = double.NaN, Usage = double.NaN, MemUsage = double.NaN;
        public ulong MemUsed, MemTotal;
        public double Power = double.NaN, PowerLimit = double.NaN;
        public double CoreMHz = double.NaN, MemMHz = double.NaN, Fan = double.NaN;
        public double Encoder = double.NaN, Decoder = double.NaN;
        public string? PState, Pcie, Limit;
    }

    public static Live Sample(IntPtr h)
    {
        var l = new Live();
        try
        {
            uint u;
            if (nvmlDeviceGetTemperature(h, 0, out u) == 0) l.Temp = u;
            if (nvmlDeviceGetUtilizationRates(h, out var ut) == 0) { l.Usage = ut.Gpu; l.MemUsage = ut.Memory; }
            if (nvmlDeviceGetMemoryInfo(h, out var m) == 0) { l.MemUsed = m.Used; l.MemTotal = m.Total; }
            if (nvmlDeviceGetPowerUsage(h, out u) == 0) l.Power = u / 1000.0;
            if (nvmlDeviceGetEnforcedPowerLimit(h, out u) == 0) l.PowerLimit = u / 1000.0;
            if (nvmlDeviceGetClockInfo(h, 0, out u) == 0) l.CoreMHz = u;
            if (nvmlDeviceGetClockInfo(h, 2, out u) == 0) l.MemMHz = u;
            if (nvmlDeviceGetFanSpeed(h, out u) == 0) l.Fan = u;
            if (nvmlDeviceGetPerformanceState(h, out int ps) == 0 && ps is >= 0 and < 32) l.PState = "P" + ps;
            if (nvmlDeviceGetCurrPcieLinkGeneration(h, out uint g) == 0 && nvmlDeviceGetCurrPcieLinkWidth(h, out uint w) == 0)
                l.Pcie = $"PCIe {g}.0 x{w}";
            if (nvmlDeviceGetEncoderUtilization(h, out u, out _) == 0) l.Encoder = u;
            if (nvmlDeviceGetDecoderUtilization(h, out u, out _) == 0) l.Decoder = u;
            try
            {
                if (nvmlDeviceGetCurrentClocksThrottleReasons(h, out ulong r) == 0) l.Limit = ThrottleText(r);
            }
            catch (EntryPointNotFoundException) { }
        }
        catch (Exception ex)
        {
            Log.Error("NVML sample", ex);
        }
        return l;
    }

    private static string ThrottleText(ulong r)
    {
        if (r == 0) return "None";
        var parts = new List<string>();
        if ((r & 0x1) != 0) parts.Add("Idle");
        if ((r & 0x2) != 0) parts.Add("App clocks");
        if ((r & 0x4) != 0) parts.Add("Power");
        if ((r & 0x8) != 0) parts.Add("HW slowdown");
        if ((r & 0x10) != 0) parts.Add("Sync boost");
        if ((r & 0x20) != 0) parts.Add("Thermal");
        if ((r & 0x40) != 0) parts.Add("HW thermal");
        if ((r & 0x80) != 0) parts.Add("Power brake");
        if ((r & 0x100) != 0) parts.Add("Display clock");
        return string.Join(", ", parts);
    }
}
