using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class MemoryViewModel : PageViewModel
{
    public override string Title => "Memory";

    public ChartSeries UsageSeries { get; } = Series.New();
    public ChartSeries CommitSeries { get; } = Series.New();
    public ObservableCollection<InfoSection> Modules { get; } = new();
    public StatList Stats { get; } = new("In use", "Available", "Committed", "Cached", "Compressed", "Paged pool", "Non-paged pool",
        "Speed", "Slots used", "Form factor", "Hardware reserved", "Page faults/s", "Hard faults/s");

    [ObservableProperty] private double _loadPercent;
    [ObservableProperty] private string _loadText = "0%";
    [ObservableProperty] private string _usedText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private double _inUse;
    [ObservableProperty] private double _modified;
    [ObservableProperty] private double _standby;
    [ObservableProperty] private double _free;
    [ObservableProperty] private string _inUseText = "";
    [ObservableProperty] private string _modifiedText = "";
    [ObservableProperty] private string _standbyText = "";
    [ObservableProperty] private string _freeText = "";

    private MemoryInfo? _info;

    public void Load(MemoryInfo info)
    {
        _info = info;
        Summary = info.Summary;
        Subtitle = info.Summary + (info.FormFactor != "" ? $" · {info.FormFactor}" : "");
        Replace(Modules, info.Modules);
        Replace(Sections, info.Sections);
        Stats.Set("Speed", info.SpeedMTs > 0 ? $"{info.SpeedMTs} MT/s" : null);
        Stats.Set("Slots used", info.SlotsTotal > 0 ? $"{info.SlotsUsed} of {info.SlotsTotal}" : info.SlotsUsed.ToString());
        Stats.Set("Form factor", info.FormFactor);
        IsLoading = false;
    }

    public override void OnSnapshot(Snapshot s)
    {
        var m = s.Memory;
        UsageSeries.Push(m.Load);
        CommitSeries.Push(m.CommitLimit > 0 ? m.CommitTotal * 100.0 / m.CommitLimit : 0);
        LoadPercent = m.Load;
        LoadText = $"{m.Load:0}%";
        UsedText = Fmt.Bytes(m.Used);
        TotalText = Fmt.Bytes(m.Total);

        double total = m.Total;
        double other = m.Modified + m.Standby + m.Free;
        double inUse = Math.Max(0, total - other);
        if (other <= 0) inUse = m.Used; // counters unavailable
        InUse = inUse;
        Modified = m.Modified;
        Standby = m.Standby;
        Free = other <= 0 ? m.Available : m.Free;
        InUseText = Fmt.Bytes(inUse);
        ModifiedText = Fmt.Bytes(m.Modified);
        StandbyText = Fmt.Bytes(m.Standby);
        FreeText = Fmt.Bytes(Free);

        Stats.Set("In use", Fmt.Bytes(m.Used));
        Stats.Set("Available", Fmt.Bytes(m.Available));
        Stats.Set("Committed", $"{Fmt.Bytes(m.CommitTotal)} / {Fmt.Bytes(m.CommitLimit)}");
        Stats.Set("Cached", Fmt.Bytes(m.Standby + m.Modified > 0 ? m.Standby + m.Modified : m.Cached));
        Stats.Set("Compressed", m.Compressed > 0 ? Fmt.Bytes(m.Compressed) : null);
        Stats.Set("Paged pool", Fmt.Bytes(m.PagedPool));
        Stats.Set("Non-paged pool", Fmt.Bytes(m.NonPagedPool));
        if (_info != null && _info.InstalledBytes > m.Total)
            Stats.Set("Hardware reserved", Fmt.Bytes(_info.InstalledBytes - m.Total));
        if (!double.IsNaN(m.PageFaultsPerSec)) Stats.Set("Page faults/s", m.PageFaultsPerSec.ToString("N0"));
        if (!double.IsNaN(m.PagesPerSec)) Stats.Set("Hard faults/s", m.PagesPerSec.ToString("N0"));
    }

    public override IEnumerable<InfoSection> ReportSections() =>
        Sections.Concat(Modules).Append(Stats.ToSection("Live readings"));
}
