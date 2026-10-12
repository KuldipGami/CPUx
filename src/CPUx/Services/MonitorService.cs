using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CPUx.Core;

namespace CPUx.Services;

/// <summary>Background sampler: gathers a <see cref="Snapshot"/> every interval and publishes it on the UI thread.</summary>
public sealed class MonitorService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;

    private readonly PerfCategory _processor = new("Processor Information");
    private readonly PerfCategory _system = new("System");
    private readonly PerfCategory _memory = new("Memory");
    private readonly PerfCategory _gpuEngine = new("GPU Engine");
    private readonly PerfCategory _gpuMemory = new("GPU Adapter Memory");
    private readonly PerfCategory _disk = new("PhysicalDisk");
    private readonly PerfCategory _thermal = new("Thermal Zone Information");
    private readonly ProcessSampler _processes = new();

    private readonly Dictionary<string, (long Rx, long Tx, long Ticks)> _netPrev = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<(string, string)>> _netDetails = new();
    private volatile Dictionary<string, Dictionary<string, string>> _wifi = new();
    private int _netRefreshRunning;
    private readonly Dictionary<string, List<VolumeSample>> _volumes = new();
    private List<BatteryDevice>? _batteries;
    private double _batteryFullWh = double.NaN;
    private bool _batteryTempSupported = true;
    private string[]? _logicalInstances;
    private int _tick;

    private readonly Latest<List<HwNode>> _sensorSource;
    private readonly Latest<List<NetSample>> _networkSource;

    /// <summary>Runs a slow query on a worker thread; Get() never blocks and returns the newest finished result.</summary>
    private sealed class Latest<T> where T : class
    {
        private readonly string _name;
        private readonly Func<T> _query;
        private volatile T? _value;
        private int _running;

        public Latest(string name, Func<T> query)
        {
            _name = name;
            _query = query;
        }

        public T? Get()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    try { _value = _query(); }
                    catch (Exception ex) { Log.Error("Sample " + _name, ex); }
                    finally { Interlocked.Exchange(ref _running, 0); }
                    if (sw.ElapsedMilliseconds > 5000) Log.Write($"[slow] {_name} took {sw.ElapsedMilliseconds} ms");
                });
            }
            return _value;
        }
    }

    public MonitorService(Dispatcher ui)
    {
        _ui = ui;
        _sensorSource = new Latest<List<HwNode>>("sensors", () => Sensors.Sample());
        _networkSource = new Latest<List<NetSample>>("network", SampleNetwork);
        _thread = new Thread(Loop) { IsBackground = true, Name = "CPUx sampler", Priority = ThreadPriority.AboveNormal };
    }

    public static MonitorService? Instance { get; set; }

    public event Action<Snapshot>? Updated;

    public HardwareSensors Sensors { get; } = new();
    public bool Paused { get; set; }

    // Static inventory used to map live counters to devices; filled in when inventory loading finishes.
    public volatile CpuInfo? Cpu;
    public volatile List<GpuInfo> Gpus = new();
    public volatile List<DiskInfo> Disks = new();

    public void Start()
    {
        Task.Run(OpenSensors);
        _thread.Start();
    }

    /// <summary>Opens the sensor library (when enabled) and then looks for controllable fans.</summary>
    public void OpenSensors()
    {
        if (AppSettings.Current.EnableSensors) Sensors.Open();
        FanService.Instance.Discover(Sensors);
    }

    /// <summary>Forces an immediate sample (used after resume or settings changes).</summary>
    public void Wake() => _wake.Set();

    private void Loop()
    {
        // Prime the rate counters so the first published snapshot already has deltas.
        try { Collect(); } catch { }
        Thread.Sleep(400);
        while (!_stop)
        {
            var sw = Stopwatch.StartNew();
            if (!Paused)
            {
                try
                {
                    var snap = Collect();
                    _ui.BeginInvoke(DispatcherPriority.Background, () => Updated?.Invoke(snap));
                }
                catch (Exception ex)
                {
                    Log.Error("Sampler", ex);
                }
            }
            int wait = Math.Max(150, AppSettings.Current.IntervalMs - (int)sw.ElapsedMilliseconds);
            _wake.WaitOne(wait);
        }
    }

    private Snapshot Collect()
    {
        _tick++;
        var s = new Snapshot();
        // Sensor-library and network queries can block for seconds when disks or the GPU are saturated,
        // so they refresh in the background and each tick uses their latest completed result.
        List<HwNode>? hw = AppSettings.Current.EnableSensors && Sensors.IsOpen ? _sensorSource.Get() : null;
        s.Sensors = hw;
        s.Networks = _networkSource.Get() ?? new List<NetSample>();

        Safe("processes", () => s.Processes = _processes.Sample());
        Safe("cpu", () => SampleCpu(s, hw));
        Safe("memory", () => SampleMemory(s));
        Safe("gpu", () => SampleGpu(s, hw));
        Safe("disk", () => SampleDisks(s, hw));
        Safe("fans", () =>
        {
            double gpuTemp = s.Gpus.Where(g => !double.IsNaN(g.Temp)).Select(g => g.Temp).DefaultIfEmpty(double.NaN).Max();
            s.Fans = FanService.Instance.Sample(hw, s.Cpu.PackageTemp, gpuTemp);
        });
        Safe("battery", () => SampleBattery(s));
        Safe("thermal", () => SampleThermal(s));
        return s;
    }

    private static void Safe(string what, Action a)
    {
        var sw = Stopwatch.StartNew();
        try { a(); }
        catch (Exception ex) { Log.Error("Sample " + what, ex); }
        if (sw.ElapsedMilliseconds > 3000) Log.Write($"[slow] sampling {what} took {sw.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------ CPU
    private static readonly Regex CoreSensor = new(@"(Core|P-Core|E-Core) #\d+$", RegexOptions.Compiled);

    private void SampleCpu(Snapshot s, List<HwNode>? hw)
    {
        var c = s.Cpu;
        if (_processor.Read())
        {
            double total = _processor.Value("% Processor Utility", "_Total");
            if (double.IsNaN(total)) total = _processor.Value("% Processor Time", "_Total");
            c.Usage = Math.Clamp(double.IsNaN(total) ? 0 : total, 0, 100);
            double busy = _processor.Value("% Processor Time", "_Total");
            if (!double.IsNaN(busy)) c.BusyPercent = Math.Clamp(busy, 0, 100);

            double baseMhz = _processor.Raw("Processor Frequency", "_Total");
            c.BaseMHz = baseMhz;
            double perf = _processor.Value("% Processor Performance", "_Total");
            if (baseMhz > 0 && !double.IsNaN(perf)) c.MHz = baseMhz * perf / 100.0;
            c.InterruptsPerSec = _processor.Value("Interrupts/sec", "_Total");

            _logicalInstances ??= _processor.Instances("% Processor Utility")
                .Where(i => !i.Contains("_Total"))
                .Select(i => (Name: i, Parts: i.Split(',')))
                .Where(x => x.Parts.Length == 2 && int.TryParse(x.Parts[0], out _) && int.TryParse(x.Parts[1], out _))
                .OrderBy(x => int.Parse(x.Parts[0])).ThenBy(x => int.Parse(x.Parts[1]))
                .Select(x => x.Name).ToArray();
            int n = _logicalInstances.Length;
            c.LogicalUsage = new double[n];
            c.LogicalMHz = new double[n];
            for (int i = 0; i < n; i++)
            {
                double u = _processor.Value("% Processor Utility", _logicalInstances[i]);
                if (double.IsNaN(u)) u = _processor.Value("% Processor Time", _logicalInstances[i]);
                c.LogicalUsage[i] = Math.Clamp(double.IsNaN(u) ? 0 : u, 0, 100);
                double p = _processor.Value("% Processor Performance", _logicalInstances[i]);
                double f = _processor.Raw("Processor Frequency", _logicalInstances[i]);
                c.LogicalMHz[i] = f > 0 && !double.IsNaN(p) ? f * p / 100.0 : double.NaN;
            }
        }
        if (_system.Read()) c.ContextSwitchesPerSec = _system.Value("Context Switches/sec");

        var pi = Native.PerformanceInfo();
        c.Processes = (int)pi.ProcessCount;
        c.Threads = (int)pi.ThreadCount;
        c.Handles = (int)pi.HandleCount;
        c.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        var cpu = hw?.FirstOrDefault(h => h.Type == "Cpu");
        if (cpu != null)
        {
            c.PackageTemp = cpu.Find("Temperature", "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tdie", "Core Max", "Core Average");
            c.PackagePower = cpu.Find("Power", "CPU Package", "Package");
            if (c.PackagePower <= 0.01) c.PackagePower = double.NaN; // reads 0 W when MSR access is unavailable
            if (c.PackageTemp <= 0) c.PackageTemp = double.NaN;
            c.CoreVoltage = cpu.Find("Voltage", "CPU Core", "Core (SVI2 TFN)", "Vcore", "Core #1");
            if (c.CoreVoltage <= 0) c.CoreVoltage = double.NaN;
            c.CoreTemps = cpu.Sensors.Where(x => x.Type == "Temperature" && CoreSensor.IsMatch(x.Name)).Select(x => x.Value).ToArray();
            c.CoreClocks = cpu.Sensors.Where(x => x.Type == "Clock" && CoreSensor.IsMatch(x.Name)).Select(x => x.Value).ToArray();
        }
        // ASUS laptops report the CPU temperature through their firmware interface, no admin rights or driver needed.
        if (double.IsNaN(c.PackageTemp) && AsusAcpi.Available) c.PackageTemp = AsusAcpi.LatestCpuTemperature;
    }

    // ------------------------------------------------------------------ Memory
    private void SampleMemory(Snapshot s)
    {
        var m = s.Memory;
        var st = Native.MemoryStatus();
        var pi = Native.PerformanceInfo();
        ulong page = (ulong)pi.PageSize;
        m.Total = st.ullTotalPhys;
        m.Available = st.ullAvailPhys;
        m.Used = m.Total - m.Available;
        m.Load = m.Total == 0 ? 0 : m.Used * 100.0 / m.Total;
        m.CommitTotal = (ulong)pi.CommitTotal * page;
        m.CommitLimit = (ulong)pi.CommitLimit * page;
        m.Cached = (ulong)pi.SystemCache * page;
        m.PagedPool = (ulong)pi.KernelPaged * page;
        m.NonPagedPool = (ulong)pi.KernelNonpaged * page;
        if (_memory.Read())
        {
            m.Modified = (ulong)Math.Max(0, _memory.Raw("Modified Page List Bytes"));
            m.Standby = (ulong)Math.Max(0, _memory.Raw("Standby Cache Core Bytes") + _memory.Raw("Standby Cache Normal Priority Bytes") + _memory.Raw("Standby Cache Reserve Bytes"));
            m.Free = (ulong)Math.Max(0, _memory.Raw("Free & Zero Page List Bytes"));
            m.PageFaultsPerSec = _memory.Value("Page Faults/sec");
            m.PagesPerSec = _memory.Value("Pages/sec");
        }
        var compression = s.Processes?.FirstOrDefault(p => p.Name == "Memory Compression");
        if (compression != null) m.Compressed = (ulong)Math.Max(0, compression.WorkingSet);
    }

    // ------------------------------------------------------------------ GPU
    private static string Normalize(string s) =>
        Regex.Replace(s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", ""), @"[^A-Za-z0-9]", "").ToLowerInvariant();

    public static string EngineName(string type) => type switch
    {
        "3D" => "3D",
        "Copy" => "Copy",
        "VideoDecode" => "Video Decode",
        "VideoEncode" => "Video Encode",
        "VideoProcessing" => "Video Processing",
        "LegacyOverlay" => "Legacy Overlay",
        "" => "Other",
        _ => type.Replace('_', ' '),
    };

    private void SampleGpu(Snapshot s, List<HwNode>? hw)
    {
        var gpus = Gpus;
        if (gpus.Count == 0) return;
        bool haveEngines = _gpuEngine.Read();
        bool haveMemory = _gpuMemory.Read();

        // luid -> engine "type|index" -> summed utilisation
        var perLuid = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        if (haveEngines)
        {
            foreach (var inst in _gpuEngine.Instances("Utilization Percentage"))
            {
                int l = inst.IndexOf("luid_", StringComparison.Ordinal);
                int ph = inst.IndexOf("_phys_", StringComparison.Ordinal);
                int en = inst.IndexOf("_eng_", StringComparison.Ordinal);
                int ty = inst.IndexOf("_engtype_", StringComparison.Ordinal);
                if (l < 0 || ph < l || en < ph || ty < en) continue;
                string luid = inst[l..ph];
                string key = inst[(ty + 9)..] + "|" + inst[(en + 5)..ty];
                double v = _gpuEngine.Value("Utilization Percentage", inst);
                if (double.IsNaN(v)) v = 0;
                if (!perLuid.TryGetValue(luid, out var engines)) perLuid[luid] = engines = new();
                engines[key] = engines.GetValueOrDefault(key) + v;

                if (v > 0 && inst.StartsWith("pid_", StringComparison.Ordinal))
                {
                    int us = inst.IndexOf('_', 4);
                    if (us > 4 && int.TryParse(inst.AsSpan(4, us - 4), out int pid))
                        s.GpuByPid[pid] = Math.Min(100, Math.Max(s.GpuByPid.GetValueOrDefault(pid), v));
                }
            }
        }

        foreach (var g in gpus)
        {
            var gs = new GpuSample { Luid = g.LuidKey, DedicatedTotal = g.DedicatedBytes };
            if (perLuid.TryGetValue(g.LuidKey, out var engines))
            {
                foreach (var grp in engines.GroupBy(e => e.Key.Split('|')[0]))
                    gs.Engines[EngineName(grp.Key)] = Math.Clamp(grp.Max(e => e.Value), 0, 100);
                gs.Usage = gs.Engines.Count == 0 ? 0 : gs.Engines.Values.Max();
            }
            if (haveMemory)
            {
                foreach (var inst in _gpuMemory.Instances("Dedicated Usage"))
                {
                    if (!inst.StartsWith(g.LuidKey, StringComparison.OrdinalIgnoreCase)) continue;
                    gs.DedicatedUsed += (ulong)Math.Max(0, _gpuMemory.Raw("Dedicated Usage", inst));
                    gs.SharedUsed += (ulong)Math.Max(0, _gpuMemory.Raw("Shared Usage", inst));
                }
            }

            if (g.NvmlDevice != null && AppSettings.Current.PollNvidia)
            {
                var n = Nvml.Sample(g.NvmlDevice.Handle);
                gs.Temp = n.Temp;
                gs.Power = n.Power;
                gs.PowerLimit = n.PowerLimit;
                gs.CoreMHz = n.CoreMHz;
                gs.MemMHz = n.MemMHz;
                gs.FanPercent = n.Fan;
                gs.PState = n.PState;
                gs.Pcie = n.Pcie;
                gs.Encoder = n.Encoder;
                gs.Decoder = n.Decoder;
                gs.Limit = n.Limit;
                if (n.MemTotal > 0) gs.DedicatedTotal = n.MemTotal;
                if (!haveEngines && !double.IsNaN(n.Usage)) gs.Usage = n.Usage;
            }

            var node = hw?.FirstOrDefault(h => h.Type.StartsWith("Gpu", StringComparison.Ordinal) && Normalize(h.Name) == Normalize(g.Name))
                       ?? hw?.FirstOrDefault(h => h.Type.StartsWith("Gpu", StringComparison.Ordinal) && Normalize(h.Name).Contains(Normalize(g.Name)));
            if (node != null)
            {
                if (double.IsNaN(gs.Temp)) gs.Temp = node.Find("Temperature", "GPU Core", "GPU Temperature", "GPU", "GPU Package");
                if (double.IsNaN(gs.Temp)) gs.Temp = node.Find("Temperature", _ => true);
                gs.HotSpot = node.Find("Temperature", "GPU Hot Spot");
                gs.MemTemp = node.Find("Temperature", "GPU Memory Junction", "GPU Memory");
                if (double.IsNaN(gs.CoreMHz)) gs.CoreMHz = node.Find("Clock", "GPU Core", "GPU Shader");
                if (double.IsNaN(gs.MemMHz)) gs.MemMHz = node.Find("Clock", "GPU Memory");
                if (double.IsNaN(gs.Power)) gs.Power = node.Find("Power", "GPU Package", "GPU Power", "GPU Core", "GPU SoC");
                gs.FanRpm = node.Find("Fan", _ => true);
                if (double.IsNaN(gs.FanPercent)) gs.FanPercent = node.Find("Control", _ => true);
            }
            s.Gpus.Add(gs);
        }
    }

    // ------------------------------------------------------------------ Disks
    private void SampleDisks(Snapshot s, List<HwNode>? hw)
    {
        if (!_disk.Read()) return;
        var disks = Disks;
        bool refreshVolumes = _tick % 5 == 1;
        foreach (var inst in _disk.Instances("% Idle Time"))
        {
            if (inst == "_Total") continue;
            var parts = inst.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out int number)) continue;
            var d = new DiskSample
            {
                Number = number,
                Instance = inst,
                Letters = parts.Skip(1).Where(p => p.Length == 2 && p[1] == ':').ToList(),
                ActivePercent = 100 - Math.Clamp(Nz(_disk.Value("% Idle Time", inst), 100), 0, 100),
                ReadBps = Math.Max(0, Nz(_disk.Value("Disk Read Bytes/sec", inst))),
                WriteBps = Math.Max(0, Nz(_disk.Value("Disk Write Bytes/sec", inst))),
                ResponseMs = Math.Max(0, Nz(_disk.Value("Avg. Disk sec/Transfer", inst)) * 1000),
                Queue = _disk.Raw("Current Disk Queue Length", inst),
            };

            var info = disks.FirstOrDefault(x => x.Number == number);
            if (info != null)
            {
                var node = hw?.FirstOrDefault(h => h.Type == "Storage" && Normalize(h.Name) == Normalize(info.Model))
                           ?? hw?.FirstOrDefault(h => h.Type == "Storage" && (Normalize(h.Name).Contains(Normalize(info.Model)) || Normalize(info.Model).Contains(Normalize(h.Name))));
                if (node != null)
                {
                    d.Temp = node.Find("Temperature", _ => true);
                    d.LifePercent = node.Find("Level", "Remaining Life", "Life");
                    if (double.IsNaN(d.LifePercent))
                    {
                        double used = node.Find("Level", "Percentage Used");
                        if (!double.IsNaN(used)) d.LifePercent = Math.Max(0, 100 - used);
                    }
                    d.DataReadGB = node.Find("Data", "Data Read", "Total Bytes Read");
                    d.DataWrittenGB = node.Find("Data", "Data Written", "Total Bytes Written");
                }
                if (info.Letters.Count > 0) d.Letters = info.Letters;
            }

            // Keyed by letters too, so a different USB drive that reuses a disk number gets fresh volumes at once.
            string volumeKey = $"{number}|{string.Join(",", d.Letters)}";
            if (refreshVolumes || !_volumes.ContainsKey(volumeKey))
            {
                var vols = new List<VolumeSample>();
                foreach (var letter in d.Letters)
                {
                    try
                    {
                        var di = new DriveInfo(letter);
                        if (!di.IsReady) continue;
                        vols.Add(new VolumeSample
                        {
                            Letter = letter, Label = di.VolumeLabel, FileSystem = di.DriveFormat,
                            Total = di.TotalSize, Free = di.AvailableFreeSpace,
                        });
                    }
                    catch { }
                }
                _volumes[volumeKey] = vols;
            }
            d.Volumes = _volumes[volumeKey];
            s.Disks.Add(d);
        }
    }

    private static double Nz(double v, double fallback = 0) => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;

    // ------------------------------------------------------------------ Network
    private static readonly string[] VirtualHints =
    {
        "virtual", "hyper-v", "vmware", "virtualbox", "loopback", "tap-", "wan miniport", "pseudo", "kernel debug",
        "wi-fi direct", "teredo", "isatap", "vethernet", "npcap", "wireguard", "tailscale", "zerotier",
    };

    private List<NetSample> SampleNetwork()
    {
        var list = new List<NetSample>();
        bool refresh = _tick % 10 == 1;
        long now = Environment.TickCount64;
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        if (refresh || nics.Any(n => !_netDetails.ContainsKey(n.Id))) RefreshDetailsInBackground(nics);
        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var n = new NetSample
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = nic.Description,
                Type = TypeName(nic.NetworkInterfaceType),
                Status = nic.OperationalStatus.ToString(),
                IsUp = nic.OperationalStatus == OperationalStatus.Up,
                IsWireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
            };
            string desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            n.IsVirtual = VirtualHints.Any(desc.Contains);
            try { n.Speed = nic.Speed; } catch { }
            try
            {
                var st = nic.GetIPStatistics();
                n.RxTotal = st.BytesReceived;
                n.TxTotal = st.BytesSent;
            }
            catch { }

            if (_netPrev.TryGetValue(nic.Id, out var prev) && now > prev.Ticks)
            {
                double secs = (now - prev.Ticks) / 1000.0;
                n.RxBps = Math.Max(0, (n.RxTotal - prev.Rx) / secs);
                n.TxBps = Math.Max(0, (n.TxTotal - prev.Tx) / secs);
            }
            _netPrev[nic.Id] = (n.RxTotal, n.TxTotal, now);

            n.Details = _netDetails.GetValueOrDefault(nic.Id);
            list.Add(n);
        }
        return list;
    }

    /// <summary>
    /// IP details and the netsh Wi-Fi query can take seconds when the machine is saturated (e.g. during a stress
    /// test), so they are refreshed off the sampler thread and never delay live readings.
    /// </summary>
    private void RefreshDetailsInBackground(NetworkInterface[] nics)
    {
        if (Interlocked.Exchange(ref _netRefreshRunning, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                if (nics.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up))
                    _wifi = ReadWifi();
                foreach (var nic in nics)
                {
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    try { _netDetails[nic.Id] = Details(nic); }
                    catch (Exception ex) { Log.Error("NIC details", ex); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _netRefreshRunning, 0);
            }
        });
    }

    private static string TypeName(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
        _ => t.ToString(),
    };

    private List<(string, string)> Details(NetworkInterface nic)
    {
        var d = new List<(string, string)>();
        var ip = nic.GetIPProperties();
        string mac = string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        d.Add(("Adapter", nic.Description));
        d.Add(("Connection type", TypeName(nic.NetworkInterfaceType)));
        d.Add(("Status", nic.OperationalStatus.ToString()));
        try { if (nic.Speed > 0) d.Add(("Link speed", Fmt.LinkSpeed(nic.Speed))); } catch { }
        if (!string.IsNullOrEmpty(mac)) d.Add(("MAC address", mac));
        var v4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
        foreach (var a in v4) d.Add(("IPv4 address", $"{a.Address}/{a.PrefixLength}"));
        var v6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).OrderBy(a => a.Address.IsIPv6LinkLocal).ToList();
        foreach (var a in v6) d.Add((a.Address.IsIPv6LinkLocal ? "Link-local IPv6" : "IPv6 address", a.Address.ToString()));
        foreach (var g in ip.GatewayAddresses.Where(g => !g.Address.Equals(System.Net.IPAddress.Any)))
            d.Add(("Default gateway", g.Address.ToString()));
        if (ip.DnsAddresses.Count > 0) d.Add(("DNS servers", string.Join(", ", ip.DnsAddresses.Select(a => a.ToString()))));
        if (!string.IsNullOrEmpty(ip.DnsSuffix)) d.Add(("DNS suffix", ip.DnsSuffix));
        try
        {
            var p4 = ip.GetIPv4Properties();
            d.Add(("DHCP", p4.IsDhcpEnabled ? "Enabled" : "Disabled"));
            if (p4.IsDhcpEnabled && ip.DhcpServerAddresses.Count > 0) d.Add(("DHCP server", string.Join(", ", ip.DhcpServerAddresses)));
            d.Add(("MTU", p4.Mtu.ToString()));
            d.Add(("Interface index", p4.Index.ToString()));
        }
        catch { }

        if (_wifi.TryGetValue(nic.Name, out var w))
        {
            foreach (var (key, label) in new[]
                     {
                         ("SSID", "SSID"), ("BSSID", "BSSID"), ("Radio type", "Radio type"), ("Band", "Band"), ("Channel", "Channel"),
                         ("Signal", "Signal"), ("Receive rate (Mbps)", "Receive rate"), ("Transmit rate (Mbps)", "Transmit rate"),
                         ("Authentication", "Authentication"), ("Cipher", "Cipher"), ("Profile", "Profile"),
                     })
            {
                if (w.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    d.Add((label, key.Contains("rate") ? v + " Mbps" : v));
            }
        }
        return d;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadWifi()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return result;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(8000))
            {
                try { p.Kill(); } catch { }
                return result;
            }
            string text = read.Result;
            Dictionary<string, string>? cur = null;
            foreach (var raw in text.Split('\n'))
            {
                int colon = raw.IndexOf(" : ", StringComparison.Ordinal);
                if (colon < 0) continue;
                string key = raw[..colon].Trim(), value = raw[(colon + 3)..].Trim();
                if (key == "Name")
                {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[value] = cur;
                }
                else if (cur != null && !cur.ContainsKey(key))
                {
                    cur[key] = value;
                }
            }
        }
        catch (Exception ex) { Log.Error("netsh wlan", ex); }
        return result;
    }

    // ------------------------------------------------------------------ Battery
    private void SampleBattery(Snapshot s)
    {
        if (!Native.GetSystemPowerStatus(out var ps)) return;
        _batteries ??= BatteryDevice.Open();
        bool present = _batteries.Count > 0 || (ps.BatteryFlag & 128) == 0 && ps.BatteryFlag != 255;
        if (!present) return;

        var b = new BatterySample
        {
            Percent = ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent : -1,
            OnAc = ps.ACLineStatus == 1,
            Charging = (ps.BatteryFlag & 8) != 0,
            SecondsLeft = ps.BatteryLifeTime,
        };
        var dev = _batteries.FirstOrDefault();
        if (dev != null)
        {
            var st = dev.Status();
            if (st is { } x)
            {
                if (x.Capacity != uint.MaxValue) b.RemainingWh = x.Capacity / 1000.0;
                if (x.Voltage != uint.MaxValue && x.Voltage > 0) b.VoltageV = x.Voltage / 1000.0;
                if (x.Rate != int.MinValue) b.RateW = x.Rate / 1000.0;
                b.Charging = (x.PowerState & 4) != 0;
                b.OnAc = (x.PowerState & 1) != 0;
            }
            if (double.IsNaN(_batteryFullWh) || _tick % 60 == 0)
            {
                var info = dev.Information();
                if (info is { } i && i.FullChargedCapacity > 0 && i.FullChargedCapacity != uint.MaxValue) _batteryFullWh = i.FullChargedCapacity / 1000.0;
            }
            b.FullWh = _batteryFullWh;
            if (_batteryTempSupported)
            {
                b.TempC = dev.TemperatureC();
                if (double.IsNaN(b.TempC)) _batteryTempSupported = false;
            }
            if (b.Percent < 0 && b.RemainingWh > 0 && b.FullWh > 0) b.Percent = (int)Math.Round(b.RemainingWh / b.FullWh * 100);
        }
        s.Battery = b;
    }

    // ------------------------------------------------------------------ ACPI thermal zones
    private void SampleThermal(Snapshot s)
    {
        if (!_thermal.Read()) return;
        foreach (var inst in _thermal.Instances("Temperature"))
        {
            double tenths = _thermal.Raw("High Precision Temperature", inst);
            double k = tenths > 0 ? tenths / 10.0 : _thermal.Raw("Temperature", inst);
            if (k > 200) s.ThermalZones.Add((inst.Replace(@"\_TZ.", "").ToUpperInvariant(), k - 273.15));
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        try { _thread.Join(1500); } catch { }
        Sensors.Dispose();
        if (_batteries != null) foreach (var b in _batteries) b.Dispose();
        Nvml.Shutdown();
    }
}
