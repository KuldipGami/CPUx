namespace CPUx.Services;

/// <summary>One tick worth of live data, produced on the sampler thread and consumed on the UI thread.</summary>
public sealed class Snapshot
{
    public DateTime Time { get; } = DateTime.Now;
    public CpuSample Cpu { get; } = new();
    public MemorySample Memory { get; } = new();
    public List<GpuSample> Gpus { get; } = new();
    public List<DiskSample> Disks { get; } = new();
    public List<NetSample> Networks { get; set; } = new();
    public BatterySample? Battery { get; set; }
    public List<HwNode>? Sensors { get; set; }
    public List<(string Name, double Celsius)> ThermalZones { get; } = new();
    public List<ProcSample>? Processes { get; set; }
    public List<FanReading> Fans { get; set; } = new();
    public Dictionary<int, double> GpuByPid { get; } = new();
}

public sealed class CpuSample
{
    public double Usage;
    public double BusyPercent = double.NaN; // time-based busy %, not frequency-weighted
    public double[] LogicalUsage = Array.Empty<double>();
    public double[] LogicalMHz = Array.Empty<double>();
    public double MHz = double.NaN;
    public double BaseMHz;
    public int Processes, Threads, Handles;
    public TimeSpan Uptime;
    public double PackageTemp = double.NaN;
    public double PackagePower = double.NaN;
    public double CoreVoltage = double.NaN;
    public double[] CoreTemps = Array.Empty<double>();
    public double[] CoreClocks = Array.Empty<double>();
    public double InterruptsPerSec = double.NaN;
    public double ContextSwitchesPerSec = double.NaN;
}

public sealed class MemorySample
{
    public ulong Total, Available, Used;
    public double Load;
    public ulong CommitTotal, CommitLimit, Cached, PagedPool, NonPagedPool;
    public ulong Modified, Standby, Free, Compressed;
    public double PageFaultsPerSec = double.NaN, PagesPerSec = double.NaN;
}

public sealed class GpuSample
{
    public string Luid = "";
    public double Usage;
    public Dictionary<string, double> Engines = new();
    public ulong DedicatedUsed, SharedUsed, DedicatedTotal;
    public double Temp = double.NaN, HotSpot = double.NaN, MemTemp = double.NaN;
    public double CoreMHz = double.NaN, MemMHz = double.NaN;
    public double Power = double.NaN, PowerLimit = double.NaN;
    public double FanPercent = double.NaN, FanRpm = double.NaN;
    public double Encoder = double.NaN, Decoder = double.NaN;
    public string? PState, Pcie, Limit;
}

public sealed class DiskSample
{
    public int Number;
    public string Instance = "";  // performance-counter instance, e.g. "0 C: D:"
    public List<string> Letters = new();
    public double ActivePercent, ReadBps, WriteBps, ResponseMs, Queue;
    public double Temp = double.NaN;
    public double LifePercent = double.NaN;
    public double DataReadGB = double.NaN, DataWrittenGB = double.NaN;
    public List<VolumeSample> Volumes = new();
}

public sealed class VolumeSample
{
    public string Letter = "", Label = "", FileSystem = "";
    public long Total, Free;
}

public sealed class NetSample
{
    public string Id = "", Name = "", Description = "", Type = "", Status = "";
    public bool IsUp, IsWireless, IsVirtual;
    public long Speed, RxTotal, TxTotal;
    public double RxBps, TxBps;
    public List<(string Label, string Value)>? Details;
}

public sealed class BatterySample
{
    public int Percent;
    public bool OnAc, Charging;
    public int SecondsLeft = -1;
    public double RateW = double.NaN;     // + charging, - discharging
    public double VoltageV = double.NaN;
    public double RemainingWh = double.NaN;
    public double FullWh = double.NaN;
    public double TempC = double.NaN;
}

public sealed class ProcSample
{
    public int Pid, ParentPid, Threads, Handles, Session, Priority;
    public string Name = "";
    public double Cpu;
    public long PrivateWs, WorkingSet, Commit;
    public double IoBps;
    public double Gpu;
    public DateTime Started;
}
