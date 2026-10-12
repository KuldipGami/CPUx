using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class TopProcess : ObservableObject
{
    public string Name { get; init; } = "";
    public string Cpu { get; init; } = "";
    public string Memory { get; init; } = "";
    public double CpuValue { get; init; }
}

public sealed partial class VolumeVM : ObservableObject
{
    [ObservableProperty] private string _letter = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private bool _isLow;
}

/// <summary>One live tile on the dashboard: a component with a value, a caption and a history graph.</summary>
public sealed partial class DashTile : ObservableObject
{
    public DashTile(string kind, string key, string target, int order, double maximum = 100, double minimumScale = 1, string scaleUnit = "%", bool twoSeries = false)
    {
        Kind = kind;
        Key = key;
        Target = target;
        Order = order;
        Maximum = maximum;
        MinimumScale = minimumScale;
        ScaleUnit = scaleUnit;
        Chart = Series.New();
        Chart2 = twoSeries ? Series.New() : null;
    }

    /// <summary>Cpu, Gpu, Memory, Disk, Usb, Network or Battery; selects the icon and colors.</summary>
    public string Kind { get; }
    public string Key { get; }
    public string Target { get; }
    public int Order { get; }
    public double Maximum { get; }
    public double MinimumScale { get; }
    public string ScaleUnit { get; }
    public ChartSeries Chart { get; }
    public ChartSeries? Chart2 { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _badge = "";
}

public sealed partial class DashboardViewModel : PageViewModel
{
    public override string Title => "Dashboard";

    // Values shown by the mini monitor (primary GPU, busiest drive).
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private string _cpuValue = "0%";
    [ObservableProperty] private string _cpuTemp = Fmt.Dash;
    [ObservableProperty] private string _cpuName = "Processor";

    [ObservableProperty] private double _gpuUsage;
    [ObservableProperty] private string _gpuValue = "0%";
    [ObservableProperty] private string _gpuTemp = Fmt.Dash;
    [ObservableProperty] private string _gpuName = "Graphics";

    [ObservableProperty] private double _memUsage;
    [ObservableProperty] private string _memValue = "0%";

    [ObservableProperty] private string _diskSub = "";
    [ObservableProperty] private string _netSub = "";

    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _batteryValue = Fmt.Dash;
    [ObservableProperty] private string _batterySub = "";
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private string _greeting = "";

    /// <summary>Live tiles: processor, every GPU, memory, every drive (including USB drives), network and battery.</summary>
    public ObservableCollection<DashTile> Tiles { get; } = new();
    public ObservableCollection<InfoItem> Summary { get; } = new();
    public ObservableCollection<TopProcess> TopProcesses { get; } = new();
    public ObservableCollection<VolumeVM> Volumes { get; } = new();
    public StatList Health { get; } = new("CPU temperature", "GPU temperature", "CPU power", "GPU power", "Battery health", "Memory load", "Commit charge", "Processes", "Threads", "Handles");

    private string _primaryGpuLuid = "";
    private List<GpuInfo> _gpus = new();
    private List<DiskInfo> _disks = new();
    private readonly Dictionary<string, VolumeVM> _volumeMap = new();
    private readonly DashTile _cpuTile = new("Cpu", "cpu", "Processor", 0) { Title = "Processor" };
    private readonly DashTile _memTile = new("Memory", "memory", "Memory", 200) { Title = "Memory", Caption = "Physical memory in use" };
    private readonly DashTile _netTile = new("Network", "network", "Network", 400, 0, 12500, "bps", twoSeries: true) { Title = "Network", Caption = "Download · upload" };
    private DashTile? _batteryTile;

    public DashboardViewModel()
    {
        int h = DateTime.Now.Hour;
        Greeting = h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
        Subtitle = Environment.MachineName;
        foreach (var label in new[] { "Device", "Operating system", "Processor", "Graphics", "Memory", "Storage", "Motherboard", "Display", "Uptime" })
            Summary.Add(new InfoItem(label, "Loading…"));
        AddTile(_cpuTile);
        AddTile(_memTile);
        AddTile(_netTile);
    }

    private void AddTile(DashTile tile)
    {
        int idx = 0;
        while (idx < Tiles.Count && Tiles[idx].Order <= tile.Order) idx++;
        Tiles.Insert(idx, tile);
    }

    public void SetDisks(List<DiskInfo> disks) => _disks = disks;

    public void SetSummary(string label, string value)
    {
        var item = Summary.FirstOrDefault(i => i.Label == label);
        if (item != null) item.Value = Fmt.OrDash(value);
    }

    public void SetGpus(List<GpuInfo> gpus)
    {
        _gpus = gpus;
        // The mini monitor and vitals follow the discrete GPU when there is one.
        var primary = gpus.FirstOrDefault(g => !g.IsIntegrated) ?? gpus.FirstOrDefault();
        _primaryGpuLuid = primary?.LuidKey ?? "";
        GpuName = primary?.ShortName ?? "No GPU";
    }

    partial void OnCpuNameChanged(string value) => _cpuTile.Caption = value;

    public override void OnSnapshot(Snapshot s)
    {
        var c = s.Cpu;
        _cpuTile.Chart.Push(c.Usage);
        CpuUsage = c.Usage;
        CpuValue = $"{c.Usage:0}%";
        CpuTemp = Fmt.Temp(c.PackageTemp);
        _cpuTile.Value = CpuValue;
        _cpuTile.Badge = CpuTemp;
        _cpuTile.Sub = $"{Fmt.MHz(c.MHz)} · {c.Processes} processes";
        Uptime = Fmt.Duration(c.Uptime);
        SetSummary("Uptime", Uptime);

        UpdateGpuTiles(s);
        var g = s.Gpus.FirstOrDefault(x => x.Luid == _primaryGpuLuid) ?? s.Gpus.FirstOrDefault();
        if (g != null)
        {
            GpuUsage = g.Usage;
            GpuValue = $"{g.Usage:0}%";
            GpuTemp = Fmt.Temp(g.Temp);
        }

        var m = s.Memory;
        _memTile.Chart.Push(m.Load);
        MemUsage = m.Load;
        MemValue = $"{m.Load:0}%";
        _memTile.Value = MemValue;
        _memTile.Sub = $"{Fmt.Bytes(m.Used)} of {Fmt.Bytes(m.Total)} in use";

        UpdateDiskTiles(s);
        double read = s.Disks.Sum(d => d.ReadBps), write = s.Disks.Sum(d => d.WriteBps);
        DiskSub = $"R {Fmt.Rate(read)} · W {Fmt.Rate(write)}";

        var nets = s.Networks.Where(n => n.IsUp && !n.IsVirtual).ToList();
        double rx = nets.Sum(n => n.RxBps), tx = nets.Sum(n => n.TxBps);
        _netTile.Chart.Push(rx);
        _netTile.Chart2!.Push(tx);
        _netTile.Value = Fmt.Bits(rx);
        NetSub = $"↓ {Fmt.Bits(rx)}   ↑ {Fmt.Bits(tx)}";
        _netTile.Sub = NetSub;

        if (s.Battery is { } b)
        {
            HasBattery = true;
            if (_batteryTile == null)
            {
                _batteryTile = new DashTile("Battery", "battery", "Battery", 500) { Title = "Battery", Caption = "Charge level" };
                AddTile(_batteryTile);
            }
            _batteryTile.Chart.Push(Math.Max(0, b.Percent));
            BatteryValue = b.Percent >= 0 ? $"{b.Percent}%" : Fmt.Dash;
            string state = b.Charging ? "Charging" : b.OnAc ? "Plugged in" : "On battery";
            if (!double.IsNaN(b.RateW) && Math.Abs(b.RateW) > 0.05) state += $" · {Math.Abs(b.RateW):0.0} W";
            if (!b.OnAc && b.SecondsLeft > 0) state += $" · {Fmt.ShortDuration(TimeSpan.FromSeconds(b.SecondsLeft))} left";
            BatterySub = state;
            _batteryTile.Value = BatteryValue;
            _batteryTile.Sub = state;
        }

        Health.Set("CPU temperature", Fmt.Temp(c.PackageTemp));
        Health.Set("GPU temperature", g == null ? null : Fmt.Temp(g.Temp));
        Health.Set("CPU power", Fmt.Watts(c.PackagePower));
        Health.Set("GPU power", g == null ? null : Fmt.Watts(g.Power));
        Health.Set("Memory load", $"{m.Load:0}%");
        Health.Set("Commit charge", $"{Fmt.Bytes(m.CommitTotal)} / {Fmt.Bytes(m.CommitLimit)}");
        Health.Set("Processes", c.Processes.ToString("N0"));
        Health.Set("Threads", c.Threads.ToString("N0"));
        Health.Set("Handles", c.Handles.ToString("N0"));

        if (s.Processes != null)
        {
            var top = s.Processes.Where(p => p.Pid > 4).OrderByDescending(p => p.Cpu).ThenByDescending(p => p.PrivateWs).Take(6)
                .Select(p => new TopProcess { Name = p.Name, Cpu = $"{p.Cpu:0.0}%", CpuValue = p.Cpu, Memory = Fmt.Bytes(p.PrivateWs) })
                .ToList();
            Replace(TopProcesses, top);
        }

        var seen = new HashSet<string>();
        foreach (var v in s.Disks.SelectMany(d => d.Volumes).OrderBy(v => v.Letter))
        {
            seen.Add(v.Letter);
            if (!_volumeMap.TryGetValue(v.Letter, out var vm))
            {
                vm = new VolumeVM { Letter = v.Letter };
                _volumeMap[v.Letter] = vm;
                int idx = 0;
                while (idx < Volumes.Count && string.CompareOrdinal(Volumes[idx].Letter, v.Letter) < 0) idx++;
                Volumes.Insert(idx, vm);
            }
            double used = v.Total > 0 ? (v.Total - v.Free) * 100.0 / v.Total : 0;
            vm.Label = string.IsNullOrWhiteSpace(v.Label) ? "Local Disk" : v.Label;
            vm.UsedPercent = used;
            vm.IsLow = used >= 90;
            vm.Detail = $"{Fmt.Bytes(v.Free)} free of {Fmt.Bytes(v.Total)}";
        }
        foreach (var gone in _volumeMap.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Volumes.Remove(_volumeMap[gone]);
            _volumeMap.Remove(gone);
        }
        IsLoading = false;
    }

    private void UpdateGpuTiles(Snapshot s)
    {
        for (int i = 0; i < _gpus.Count; i++)
        {
            var info = _gpus[i];
            var g = s.Gpus.FirstOrDefault(x => x.Luid == info.LuidKey);
            if (g == null) continue;
            var tile = Tiles.FirstOrDefault(t => t.Kind == "Gpu" && t.Key == info.LuidKey);
            if (tile == null)
            {
                tile = new DashTile("Gpu", info.LuidKey, "Graphics#" + info.LuidKey, 100 + i)
                {
                    Title = _gpus.Count > 1 ? $"GPU {i}" : "Graphics",
                    Caption = info.ShortName,
                };
                AddTile(tile);
            }
            tile.Chart.Push(g.Usage);
            tile.Value = $"{g.Usage:0}%";
            tile.Badge = double.IsNaN(g.Temp) ? "" : Fmt.Temp(g.Temp);
            string mem = g.DedicatedTotal > 0
                ? $"VRAM {Fmt.Bytes(g.DedicatedUsed)} / {Fmt.Bytes(g.DedicatedTotal, 0)}"
                : $"Shared memory {Fmt.Bytes(g.SharedUsed)}";
            tile.Sub = info.IsIntegrated ? $"Integrated · {mem}" : double.IsNaN(g.Power) ? mem : $"{mem} · {Fmt.Watts(g.Power)}";
        }
    }

    private static readonly HashSet<string> RemovableBuses = new(StringComparer.OrdinalIgnoreCase) { "USB", "SD", "MMC", "IEEE 1394" };

    private void UpdateDiskTiles(Snapshot s)
    {
        foreach (var d in s.Disks)
        {
            var info = _disks.FirstOrDefault(x => x.Number == d.Number);
            string key = d.Number.ToString();
            var tile = Tiles.FirstOrDefault(t => t.Key == "disk" + key);
            // A different drive can take over a disk number after a USB drive is removed.
            string kind = info != null && RemovableBuses.Contains(info.Bus) ? "Usb" : "Disk";
            if (tile != null && tile.Kind != kind)
            {
                Tiles.Remove(tile);
                tile = null;
            }
            if (tile == null)
            {
                tile = new DashTile(kind, "disk" + key, "Storage", 300 + d.Number);
                AddTile(tile);
            }
            string letters = d.Letters.Count > 0 ? $" ({string.Join(" ", d.Letters)})" : "";
            tile.Title = (kind == "Usb" ? "USB drive" : "Disk " + key) + letters;
            tile.Caption = info == null ? "Detecting drive…"
                : $"{info.Model} · {(info.Media == "" ? info.Bus : $"{info.Media} ({info.Bus})")}";
            tile.Chart.Push(d.ActivePercent);
            tile.Value = $"{d.ActivePercent:0}%";
            tile.Badge = double.IsNaN(d.Temp) ? "" : Fmt.Temp(d.Temp);
            tile.Sub = $"R {Fmt.Rate(d.ReadBps)} · W {Fmt.Rate(d.WriteBps)}";
        }
        foreach (var gone in Tiles.Where(t => t.Kind is "Disk" or "Usb" && !s.Disks.Any(d => "disk" + d.Number == t.Key)).ToList())
            Tiles.Remove(gone);
    }

    public void SetBatteryHealth(BatteryInfo b)
    {
        if (b.Present && !double.IsNaN(b.WearPercent)) Health.Set("Battery health", $"{100 - b.WearPercent:0}%");
    }
}
