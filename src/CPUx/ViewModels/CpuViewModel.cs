using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class CoreTile : ObservableObject
{
    public CoreTile(int index, string kind)
    {
        Index = index;
        Kind = kind;
        Label = $"CPU {index}";
    }

    public int Index { get; }
    public string Label { get; }
    public string Kind { get; }
    public bool HasKind => Kind != "";
    public ChartSeries Series { get; } = ViewModels.Series.New();

    [ObservableProperty] private string _usage = "0%";
    [ObservableProperty] private string _clock = Fmt.Dash;
    [ObservableProperty] private string _temp = "";
}

public sealed record FeatureChip(string Name, bool Supported);

public sealed partial class CpuViewModel : PageViewModel
{
    public override string Title => "Processor";

    public ChartSeries UsageSeries { get; } = Series.New();
    public ObservableCollection<CoreTile> Cores { get; } = new();
    public ObservableCollection<FeatureChip> Features { get; } = new();
    public StatList Stats { get; } = new("Utilization", "Speed", "Base speed", "Package temperature", "Package power", "Core voltage",
        "Processes", "Threads", "Handles", "Up time", "Context switches/s", "Interrupts/s");
    public ObservableCollection<InfoItem> Badges { get; } = new();

    [ObservableProperty] private string _name = "Processor";
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private double _usage;
    [ObservableProperty] private string _usageText = "0%";
    [ObservableProperty] private string _speedText = Fmt.Dash;

    private CpuInfo? _info;

    public void Load(CpuInfo info)
    {
        _info = info;
        Name = info.Name;
        Vendor = info.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "INTEL"
               : info.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD" : info.Vendor.ToUpperInvariant();
        Subtitle = string.Join(" · ", new[] { info.Codename, $"{info.Cores} cores", $"{info.Threads} threads", info.Socket }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        Badges.Clear();
        Badges.Add(new InfoItem("Cores", info.Hybrid ? $"{info.Cores} ({info.PerformanceCores}P + {info.EfficiencyCores}E)" : info.Cores.ToString()));
        Badges.Add(new InfoItem("Threads", info.Threads.ToString()));
        Badges.Add(new InfoItem("Base clock", Fmt.MHz(info.BaseMHz)));
        if (info.L2 != "") Badges.Add(new InfoItem("L2 cache", info.L2));
        if (info.L3 != "") Badges.Add(new InfoItem("L3 cache", info.L3));
        Badges.Add(new InfoItem("Virtualization", Fmt.OnOff(info.Virtualization)));

        Replace(Features, info.InstructionSets.Select(f => new FeatureChip(f.Name, f.Supported)));
        Replace(Sections, info.Sections);
        Stats.Set("Base speed", Fmt.MHz(info.BaseMHz));
        IsLoading = false;
    }

    public override void OnSnapshot(Snapshot s)
    {
        var c = s.Cpu;
        UsageSeries.Push(c.Usage);
        Usage = c.Usage;
        UsageText = $"{c.Usage:0}%";
        SpeedText = Fmt.MHz(c.MHz);

        Stats.Set("Utilization", $"{c.Usage:0}%");
        Stats.Set("Speed", Fmt.MHz(c.MHz));
        if (c.BaseMHz > 0 && (_info == null || _info.BaseMHz <= 0)) Stats.Set("Base speed", Fmt.MHz(c.BaseMHz));
        Stats.Set("Package temperature", Fmt.Temp(c.PackageTemp));
        Stats.Set("Package power", Fmt.Watts(c.PackagePower));
        Stats.Set("Core voltage", Fmt.Volts(c.CoreVoltage));
        Stats.Set("Processes", c.Processes.ToString("N0"));
        Stats.Set("Threads", c.Threads.ToString("N0"));
        Stats.Set("Handles", c.Handles.ToString("N0"));
        Stats.Set("Up time", Fmt.Duration(c.Uptime));
        if (!double.IsNaN(c.ContextSwitchesPerSec)) Stats.Set("Context switches/s", c.ContextSwitchesPerSec.ToString("N0"));
        if (!double.IsNaN(c.InterruptsPerSec)) Stats.Set("Interrupts/s", c.InterruptsPerSec.ToString("N0"));

        int n = c.LogicalUsage.Length;
        if (Cores.Count != n)
        {
            Cores.Clear();
            for (int i = 0; i < n; i++)
            {
                string kind = "";
                if (_info is { Hybrid: true } && i < _info.LogicalIsEfficiency.Length)
                    kind = _info.LogicalIsEfficiency[i] ? "E" : "P";
                Cores.Add(new CoreTile(i, kind));
            }
        }
        for (int i = 0; i < n; i++)
        {
            var t = Cores[i];
            t.Series.Push(c.LogicalUsage[i]);
            t.Usage = $"{c.LogicalUsage[i]:0}%";
            int core = _info != null && i < _info.CoreOfLogical.Length ? _info.CoreOfLogical[i] : -1;
            double clock = core >= 0 && core < c.CoreClocks.Length ? c.CoreClocks[core] : double.NaN;
            if (double.IsNaN(clock) && i < c.LogicalMHz.Length) clock = c.LogicalMHz[i];
            t.Clock = Fmt.MHz(clock);
            double temp = core >= 0 && core < c.CoreTemps.Length ? c.CoreTemps[core] : double.NaN;
            t.Temp = double.IsNaN(temp) ? "" : Fmt.Temp(temp);
        }
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        var live = Stats.ToSection("Live readings");
        var isa = new InfoSection("Instruction sets");
        isa.Add("Supported", string.Join(", ", Features.Where(f => f.Supported).Select(f => f.Name)));
        return Sections.Append(isa).Append(live);
    }
}
