using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class BoardViewModel : PageViewModel
{
    public override string Title => "Motherboard";

    private readonly InfoSection _sensors = new("Board sensors");
    private bool _sensorsAdded;

    public void Load(List<InfoSection> sections, string summary)
    {
        Replace(Sections, sections);
        Subtitle = summary;
        if (_sensorsAdded) Sections.Add(_sensors);
        IsLoading = false;
    }

    public override void OnSnapshot(Snapshot s)
    {
        if (s.Sensors == null) return;
        foreach (var node in s.Sensors.Where(h => h.Type is "Motherboard" or "SuperIO" or "EmbeddedController" or "Cooler"))
        {
            foreach (var sensor in node.AllSensors)
            {
                if (double.IsNaN(sensor.Value)) continue;
                _sensors.Set(sensor.Name, SensorFormat.Value(sensor.Type, sensor.Value));
            }
        }
        if (!_sensorsAdded && _sensors.Items.Count > 0)
        {
            _sensorsAdded = true;
            Sections.Add(_sensors);
        }
    }
}

public sealed partial class SystemViewModel : PageViewModel
{
    public override string Title => "Operating System";

    private InfoItem? _uptime;

    public void Load(List<InfoSection> sections, string summary)
    {
        Replace(Sections, sections);
        Subtitle = summary;
        var win = Sections.FirstOrDefault();
        if (win != null)
        {
            _uptime = new InfoItem("Uptime", "");
            int idx = win.Items.ToList().FindIndex(i => i.Label == "Last boot");
            win.Items.Insert(idx >= 0 ? idx + 1 : win.Items.Count, _uptime);
        }
        IsLoading = false;
    }

    public override void OnSnapshot(Snapshot s)
    {
        if (_uptime != null) _uptime.Value = Fmt.Duration(s.Cpu.Uptime);
    }
}

public sealed partial class BatteryViewModel : PageViewModel
{
    public override string Title => "Battery";

    public ChartSeries ChargeSeries { get; } = Series.New();
    public ChartSeries RateSeries { get; } = Series.New();
    public StatList Stats { get; } = new("Status", "Time remaining", "Power", "Voltage", "Remaining capacity", "Full charge capacity",
        "Design capacity", "Wear level", "Cycle count", "Temperature");

    [ObservableProperty] private bool _present;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _percentText = Fmt.Dash;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _healthText = Fmt.Dash;
    [ObservableProperty] private double _health;

    private BatteryInfo? _info;

    public void Load(BatteryInfo info)
    {
        _info = info;
        Present = info.Present;
        Replace(Sections, info.Sections);
        Subtitle = info.Present ? info.Name : "No battery detected";
        if (!double.IsNaN(info.WearPercent))
        {
            Health = 100 - info.WearPercent;
            HealthText = $"{Health:0}%";
        }
        Stats.Set("Design capacity", double.IsNaN(info.DesignWh) ? null : $"{info.DesignWh:0.0} Wh");
        Stats.Set("Wear level", double.IsNaN(info.WearPercent) ? null : $"{info.WearPercent:0.0}%");
        Stats.Set("Cycle count", info.CycleCount > 0 ? info.CycleCount.ToString() : null);
        IsLoading = false;
    }

    public override void OnSnapshot(Snapshot s)
    {
        if (s.Battery is not { } b) return;
        Present = true;
        Percent = Math.Max(0, b.Percent);
        PercentText = b.Percent >= 0 ? $"{b.Percent}%" : Fmt.Dash;
        ChargeSeries.Push(Percent);
        RateSeries.Push(double.IsNaN(b.RateW) ? 0 : Math.Abs(b.RateW));

        State = b.Charging ? "Charging" : b.OnAc ? (b.Percent >= 95 ? "Fully charged" : "Plugged in, not charging") : "On battery";
        Stats.Set("Status", State);
        if (!b.OnAc && b.SecondsLeft > 0) Stats.Set("Time remaining", Fmt.ShortDuration(TimeSpan.FromSeconds(b.SecondsLeft)));
        else if (b.Charging && b.RateW > 0.5 && b.FullWh > 0 && b.RemainingWh >= 0)
            Stats.Set("Time remaining", Fmt.ShortDuration(TimeSpan.FromHours((b.FullWh - b.RemainingWh) / b.RateW)) + " to full");
        else Stats.Set("Time remaining", b.OnAc ? "On AC power" : Fmt.Dash);

        string rate = double.IsNaN(b.RateW) ? Fmt.Dash
            : Math.Abs(b.RateW) < 0.05 ? "0 W"
            : b.RateW > 0 ? $"+{b.RateW:0.0} W (charging)" : $"{b.RateW:0.0} W (discharging)";
        Stats.Set("Power", rate);
        Stats.Set("Voltage", double.IsNaN(b.VoltageV) ? null : $"{b.VoltageV:0.00} V");
        Stats.Set("Remaining capacity", double.IsNaN(b.RemainingWh) ? null : $"{b.RemainingWh:0.0} Wh");
        Stats.Set("Full charge capacity", double.IsNaN(b.FullWh) ? null : $"{b.FullWh:0.0} Wh");
        Stats.Set("Temperature", Fmt.Temp(b.TempC));
        if (_info != null && !double.IsNaN(b.FullWh) && _info.DesignWh > 0)
        {
            Health = Math.Min(100, b.FullWh / _info.DesignWh * 100);
            HealthText = $"{Health:0}%";
        }
    }

    public override IEnumerable<InfoSection> ReportSections() => Sections.Append(Stats.ToSection("Live readings"));
}

public sealed partial class DisplayItem : ObservableObject
{
    public DisplayItem(DisplayInfo info) => Info = info;
    public DisplayInfo Info { get; }
    public string Name => Info.Name;
    public string Summary => Info.Summary;
    public bool Primary => Info.Primary;
    public string Connection => Info.Connection;
    public IEnumerable<InfoSection> Sections => Info.Sections;

    // Layout preview (canvas units)
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _w;
    [ObservableProperty] private double _h;
    public string Badge { get; set; } = "";
}

public sealed partial class DisplayViewModel : PageViewModel
{
    public override string Title => "Displays";

    public ObservableCollection<DisplayItem> Displays { get; } = new();
    [ObservableProperty] private double _canvasWidth = 400;
    [ObservableProperty] private double _canvasHeight = 160;

    public void Load(List<DisplayInfo> displays)
    {
        var items = displays.Select((d, i) => new DisplayItem(d) { Badge = (i + 1).ToString() }).ToList();
        if (items.Count > 0)
        {
            double minX = displays.Min(d => d.X), minY = displays.Min(d => d.Y);
            double maxX = displays.Max(d => d.X + Math.Max(1, d.Width)), maxY = displays.Max(d => d.Y + Math.Max(1, d.Height));
            double scale = Math.Min(520 / (maxX - minX), 170 / (maxY - minY));
            foreach (var it in items)
            {
                it.X = (it.Info.X - minX) * scale;
                it.Y = (it.Info.Y - minY) * scale;
                it.W = Math.Max(30, it.Info.Width * scale - 6);
                it.H = Math.Max(20, it.Info.Height * scale - 6);
            }
            CanvasWidth = (maxX - minX) * scale;
            CanvasHeight = (maxY - minY) * scale;
        }
        Replace(Displays, items);
        Subtitle = items.Count == 1 ? items[0].Summary : $"{items.Count} active displays";
        IsLoading = false;
    }

    public override IEnumerable<InfoSection> ReportSections() =>
        Displays.SelectMany(d => d.Sections.Select(s => new InfoSection($"{d.Name} — {s.Title}").Also(x => { foreach (var i in s.Items) x.Add(i.Label, i.Value); })));
}

public static class SensorFormat
{
    public static string Value(string type, double v)
    {
        if (double.IsNaN(v)) return Fmt.Dash;
        return type switch
        {
            "Temperature" => AppSettings.Current.Fahrenheit ? $"{v * 9 / 5 + 32:0.0} °F" : $"{v:0.0} °C",
            "Clock" or "Frequency" => v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz",
            "Load" or "Control" or "Level" or "Humidity" => $"{v:0.0}%",
            "Voltage" => $"{v:0.000} V",
            "Current" => $"{v:0.000} A",
            "Power" => $"{v:0.0} W",
            "Fan" => $"{v:0} RPM",
            "Data" => $"{v:N1} GB",
            "SmallData" => $"{v:N0} MB",
            "Throughput" => Fmt.Rate(v),
            "Energy" => $"{v:N0} mWh",
            "TimeSpan" => Fmt.Duration(TimeSpan.FromSeconds(v)),
            "Factor" => $"{v:0.000}",
            "Flow" => $"{v:0.0} L/h",
            "Noise" => $"{v:0.0} dBA",
            "Conductivity" => $"{v:0.0} µS/cm",
            _ => $"{v:0.##}",
        };
    }

    public static string GroupName(string type) => type switch
    {
        "Temperature" => "Temperatures",
        "Clock" => "Clocks",
        "Load" => "Load",
        "Voltage" => "Voltages",
        "Current" => "Currents",
        "Power" => "Power",
        "Fan" => "Fans",
        "Control" => "Fan control",
        "Data" or "SmallData" => "Data",
        "Throughput" => "Throughput",
        "Level" => "Levels",
        "Energy" => "Energy",
        "Frequency" => "Frequencies",
        "TimeSpan" => "Time",
        "Factor" => "Factors",
        _ => type,
    };

    public static int GroupRank(string type) => type switch
    {
        "Temperature" => 0, "Load" => 1, "Clock" => 2, "Power" => 3, "Voltage" => 4, "Current" => 5,
        "Fan" => 6, "Control" => 7, "Data" => 8, "SmallData" => 9, "Throughput" => 10, "Level" => 11, _ => 20,
    };

    public static string IconFor(string hwType) => hwType switch
    {
        "Cpu" => "I.Cpu",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "I.Gpu",
        "Memory" => "I.Memory",
        "Storage" => "I.Storage",
        "Motherboard" or "SuperIO" or "EmbeddedController" => "I.Board",
        "Battery" => "I.Battery",
        "Network" => "I.Network",
        "Cooler" => "I.Fan",
        "Psu" => "I.Bolt",
        _ => "I.Thermometer",
    };
}
