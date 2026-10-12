using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class SensorRow : ObservableObject
{
    public SensorRow(string id, string name, string type)
    {
        Id = id;
        Name = name;
        Type = type;
    }

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    [ObservableProperty] private string _value = Fmt.Dash;
    [ObservableProperty] private string _min = Fmt.Dash;
    [ObservableProperty] private string _max = Fmt.Dash;
    [ObservableProperty] private bool _isVisible = true;
    public bool HasValue { get; set; }
}

public sealed partial class SensorGroup : ObservableObject
{
    public SensorGroup(string type)
    {
        Type = type;
        Name = SensorFormat.GroupName(type);
    }

    public string Type { get; }
    public string Name { get; }
    public ObservableCollection<SensorRow> Rows { get; } = new();
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class SensorHardware : ObservableObject
{
    public SensorHardware(string id, string name, string type)
    {
        Id = id;
        Name = name;
        Type = type;
        Icon = SensorFormat.IconFor(type);
    }

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Icon { get; }
    public ObservableCollection<SensorGroup> Groups { get; } = new();
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private int _count;
}

public sealed partial class SensorsViewModel : PageViewModel
{
    public override string Title => "Sensors";

    public ObservableCollection<SensorHardware> Hardware { get; } = new();
    private readonly Dictionary<string, SensorRow> _rows = new();
    private string _signature = "";

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isAdmin = SystemInfoReader.IsAdmin();
    [ObservableProperty] private bool _pawnIo = HardwareSensors.IsPawnIoInstalled();
    [ObservableProperty] private bool _showBanner;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private int _sensorCount;
    [ObservableProperty] private bool _hideEmpty = true;

    partial void OnHideEmptyChanged(bool value) => ApplyFilter();

    public SensorsViewModel()
    {
        UpdateBanner();
    }

    private void UpdateBanner()
    {
        if (!AppSettings.Current.EnableSensors)
        {
            ShowBanner = true;
            BannerText = "Hardware sensors are turned off in Settings. Only ACPI thermal zones are shown.";
        }
        else if (!IsAdmin && !PawnIo)
        {
            ShowBanner = true;
            BannerText = "Running without administrator rights and without the PawnIO driver. CPU temperatures, voltages, fan speeds and SMART data need both: restart CPUx as administrator and install PawnIO (pawnio.eu).";
        }
        else if (!IsAdmin)
        {
            ShowBanner = true;
            BannerText = "Restart CPUx as administrator to read CPU, motherboard and drive sensors.";
        }
        else if (!PawnIo)
        {
            ShowBanner = true;
            BannerText = "The PawnIO driver isn't installed, so CPU temperatures, clocks per core and motherboard sensors are unavailable. Install it from pawnio.eu and restart CPUx.";
        }
        else ShowBanner = false;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ResetMinMax()
    {
        MonitorService.Instance?.Sensors.ResetMinMax();
        foreach (var r in _rows.Values) { r.Min = Fmt.Dash; r.Max = Fmt.Dash; }
    }

    [RelayCommand]
    private void ExpandAll(string? expand)
    {
        bool e = expand != "false";
        foreach (var h in Hardware) h.IsExpanded = e;
    }

    public override void OnSnapshot(Snapshot s)
    {
        var nodes = new List<HwNode>(s.Sensors ?? new List<HwNode>());
        if (s.ThermalZones.Count > 0)
        {
            var tz = new HwNode { Id = "/acpi/thermal", Name = "ACPI Thermal Zones", Type = "Acpi" };
            foreach (var (name, c) in s.ThermalZones)
                tz.Sensors.Add(new SensorReading("/acpi/thermal/" + name, name, "Temperature", c, double.NaN, double.NaN));
            nodes.Add(tz);
        }

        // Flatten sub-hardware into its parent card for a cleaner layout.
        var flat = nodes.Select(n => (Node: n, Sensors: n.AllSensors.ToList())).Where(x => x.Sensors.Count > 0).ToList();
        string sig = string.Join("|", flat.Select(f => f.Node.Id + ":" + f.Sensors.Count));
        if (sig != _signature)
        {
            _signature = sig;
            Rebuild(flat);
        }

        bool changed = false;
        foreach (var (_, sensors) in flat)
        {
            foreach (var r in sensors)
            {
                if (!_rows.TryGetValue(r.Id, out var row)) continue;
                row.Value = SensorFormat.Value(r.Type, r.Value);
                row.Min = SensorFormat.Value(r.Type, r.Min);
                row.Max = SensorFormat.Value(r.Type, r.Max);
                bool has = !double.IsNaN(r.Value) || !double.IsNaN(r.Max);
                if (has && !row.HasValue) { row.HasValue = true; changed = true; }
            }
        }
        if (changed) ApplyFilter();
        IsLoading = false;
    }

    private void Rebuild(List<(HwNode Node, List<SensorReading> Sensors)> flat)
    {
        _rows.Clear();
        var list = new List<SensorHardware>();
        foreach (var (node, sensors) in flat)
        {
            var hw = new SensorHardware(node.Id, node.Name, node.Type) { Count = sensors.Count };
            foreach (var g in sensors.GroupBy(x => x.Type).OrderBy(g => SensorFormat.GroupRank(g.Key)))
            {
                var group = new SensorGroup(g.Key);
                foreach (var r in g)
                {
                    var row = new SensorRow(r.Id, r.Name, r.Type);
                    _rows[r.Id] = row;
                    group.Rows.Add(row);
                }
                hw.Groups.Add(group);
            }
            list.Add(hw);
        }
        Replace(Hardware, list.OrderBy(h => HwRank(h.Type)));
        SensorCount = _rows.Count;
        ApplyFilter();
    }

    private static int HwRank(string type) => type switch
    {
        "Cpu" => 0, "GpuNvidia" or "GpuAmd" or "GpuIntel" => 1, "Memory" => 2, "Motherboard" => 3, "SuperIO" => 4,
        "EmbeddedController" => 5, "Storage" => 6, "Battery" => 7, "Cooler" => 8, "Psu" => 9, _ => 10,
    };

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        foreach (var hw in Hardware)
        {
            bool hwMatch = f.Length == 0 || hw.Name.Contains(f, StringComparison.OrdinalIgnoreCase);
            bool any = false;
            int visibleRows = 0;
            foreach (var g in hw.Groups)
            {
                bool groupAny = false;
                foreach (var r in g.Rows)
                {
                    r.IsVisible = (hwMatch || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || g.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                                  && (!HideEmpty || r.HasValue);
                    if (r.IsVisible) visibleRows++;
                    groupAny |= r.IsVisible;
                }
                g.IsVisible = groupAny;
                any |= groupAny;
            }
            hw.IsVisible = any;
            hw.Count = visibleRows;
        }
        int reporting = _rows.Values.Count(r => r.HasValue);
        Subtitle = $"{reporting} of {SensorCount} sensors reporting across {Hardware.Count} devices";
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        foreach (var hw in Hardware)
        {
            var s = new InfoSection($"Sensors — {hw.Name}");
            foreach (var g in hw.Groups)
                foreach (var r in g.Rows)
                    s.Add($"{g.Name}: {r.Name}", $"{r.Value} (min {r.Min}, max {r.Max})");
            yield return s;
        }
    }
}
