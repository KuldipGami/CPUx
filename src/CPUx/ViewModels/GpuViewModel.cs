using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class EngineTile : ObservableObject
{
    public EngineTile(string name) => Name = name;
    public string Name { get; }
    public ChartSeries Series { get; } = ViewModels.Series.New();
    [ObservableProperty] private string _value = "0%";
}

public sealed partial class GpuItem : ObservableObject
{
    private static readonly string[] EngineOrder = { "3D", "Copy", "Video Encode", "Video Decode", "Video Processing", "Compute 0", "Compute 1", "Cuda" };

    public GpuItem(GpuInfo info)
    {
        Info = info;
        Name = info.ShortName;
        Kind = info.IsIntegrated ? "Integrated" : "Discrete";
        Sections = new ObservableCollection<InfoSection>(info.Sections);
    }

    public GpuInfo Info { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Vendor => Info.Vendor;
    public string Driver => Info.DriverVersion;
    public ObservableCollection<InfoSection> Sections { get; }

    public ChartSeries UsageSeries { get; } = Series.New();
    public ChartSeries DedicatedSeries { get; } = Series.New();
    public ChartSeries SharedSeries { get; } = Series.New();
    public ChartSeries TempSeries { get; } = Series.New();
    public ChartSeries PowerSeries { get; } = Series.New();
    public ObservableCollection<EngineTile> Engines { get; } = new();
    public StatList Stats { get; } = new("Utilization", "Dedicated memory", "Shared memory", "Temperature", "Hot spot", "Memory temperature",
        "Core clock", "Memory clock", "Power", "Fan", "Performance state", "PCIe link", "Encoder", "Decoder", "Perf limit");

    [ObservableProperty] private double _usage;
    [ObservableProperty] private string _usageText = "0%";
    [ObservableProperty] private string _dedicatedText = Fmt.Dash;
    [ObservableProperty] private string _sharedText = Fmt.Dash;
    [ObservableProperty] private string _tempText = Fmt.Dash;
    [ObservableProperty] private string _powerText = Fmt.Dash;
    [ObservableProperty] private double _dedicatedPercent;
    [ObservableProperty] private bool _hasTemp;
    [ObservableProperty] private bool _hasPower;

    public void Update(GpuSample g)
    {
        UsageSeries.Push(g.Usage);
        Usage = g.Usage;
        UsageText = $"{g.Usage:0}%";
        ulong total = g.DedicatedTotal > 0 ? g.DedicatedTotal : Info.DedicatedBytes;
        DedicatedPercent = total > 0 ? g.DedicatedUsed * 100.0 / total : 0;
        DedicatedSeries.Push(DedicatedPercent);
        SharedSeries.Push(Info.SharedBytes > 0 ? g.SharedUsed * 100.0 / Info.SharedBytes : 0);
        DedicatedText = total > 0 ? $"{Fmt.Bytes(g.DedicatedUsed)} / {Fmt.Bytes(total)}" : Fmt.Bytes(g.DedicatedUsed);
        SharedText = $"{Fmt.Bytes(g.SharedUsed)} / {Fmt.Bytes(Info.SharedBytes)}";

        HasTemp = !double.IsNaN(g.Temp);
        TempText = Fmt.Temp(g.Temp);
        if (HasTemp) TempSeries.Push(g.Temp);
        HasPower = !double.IsNaN(g.Power);
        PowerText = Fmt.Watts(g.Power);
        if (HasPower) PowerSeries.Push(g.Power);

        Stats.Set("Utilization", $"{g.Usage:0}%");
        Stats.Set("Dedicated memory", DedicatedText);
        Stats.Set("Shared memory", SharedText);
        Stats.Set("Temperature", Fmt.Temp(g.Temp));
        Stats.Set("Hot spot", Fmt.Temp(g.HotSpot));
        Stats.Set("Memory temperature", Fmt.Temp(g.MemTemp));
        Stats.Set("Core clock", Fmt.MHz(g.CoreMHz));
        Stats.Set("Memory clock", Fmt.MHz(g.MemMHz));
        Stats.Set("Power", double.IsNaN(g.Power) ? null : double.IsNaN(g.PowerLimit) ? Fmt.Watts(g.Power) : $"{Fmt.Watts(g.Power)} / {Fmt.Watts(g.PowerLimit)}");
        string fan = string.Join(" · ", new[] { double.IsNaN(g.FanPercent) ? null : $"{g.FanPercent:0}%", double.IsNaN(g.FanRpm) ? null : Fmt.Rpm(g.FanRpm) }.Where(x => x != null));
        Stats.Set("Fan", fan);
        Stats.Set("Performance state", g.PState);
        Stats.Set("PCIe link", g.Pcie);
        Stats.Set("Encoder", double.IsNaN(g.Encoder) ? null : $"{g.Encoder:0}%");
        Stats.Set("Decoder", double.IsNaN(g.Decoder) ? null : $"{g.Decoder:0}%");
        Stats.Set("Perf limit", g.Limit);

        foreach (var (name, value) in g.Engines)
        {
            var tile = Engines.FirstOrDefault(e => e.Name == name);
            if (tile == null)
            {
                tile = new EngineTile(name);
                int rank = Rank(name), idx = 0;
                while (idx < Engines.Count && Rank(Engines[idx].Name) <= rank) idx++;
                Engines.Insert(idx, tile);
            }
            tile.Series.Push(value);
            tile.Value = $"{value:0}%";
        }
    }

    private static int Rank(string engine)
    {
        int i = Array.IndexOf(EngineOrder, engine);
        return i < 0 ? 100 : i;
    }
}

public sealed partial class GpuViewModel : PageViewModel
{
    public override string Title => "Graphics";

    public ObservableCollection<GpuItem> Gpus { get; } = new();
    [ObservableProperty] private GpuItem? _selected;
    [ObservableProperty] private bool _hasMultiple;

    public void Load(IEnumerable<GpuInfo> gpus)
    {
        Replace(Gpus, gpus.Select(g => new GpuItem(g)));
        HasMultiple = Gpus.Count > 1;
        Selected = Gpus.FirstOrDefault();
        Subtitle = Gpus.Count switch
        {
            0 => "No graphics adapters found",
            1 => Gpus[0].Vendor,
            _ => $"{Gpus.Count} graphics adapters",
        };
        IsLoading = false;
    }

    public override void OnSnapshot(Snapshot s)
    {
        foreach (var g in s.Gpus)
            Gpus.FirstOrDefault(x => x.Info.LuidKey == g.Luid)?.Update(g);
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        foreach (var g in Gpus)
        {
            foreach (var sec in g.Sections)
                yield return new InfoSection($"{g.Name} — {sec.Title}").Also(x => { foreach (var i in sec.Items) x.Add(i.Label, i.Value); });
            yield return g.Stats.ToSection($"{g.Name} — Live readings");
        }
    }
}

internal static class FluentExtensions
{
    public static T Also<T>(this T obj, Action<T> action)
    {
        action(obj);
        return obj;
    }
}
