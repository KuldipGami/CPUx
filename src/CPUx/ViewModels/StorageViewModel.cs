using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class DiskItem : ObservableObject
{
    public DiskItem(DiskInfo info)
    {
        Info = info;
        Title = info.Model;
        Media = info.Media == "" ? "Disk" : info.Media;
        Bus = info.Bus;
        Size = Fmt.Bytes(info.SizeBytes);
        Health = info.Health;
        IsHealthy = info.Health is "Healthy" or "OK";
        Header = $"Disk {info.Number}" + (info.Letters.Count > 0 ? $" ({string.Join(" ", info.Letters)})" : "");
        Sections = new ObservableCollection<InfoSection>(info.Sections);
    }

    public DiskInfo Info { get; }
    public string Title { get; }
    public string Header { get; }
    public string Media { get; }
    public string Bus { get; }
    public string Size { get; }
    public string Health { get; }
    public bool IsHealthy { get; }
    public bool IsSystem => Info.IsSystem;
    public ObservableCollection<InfoSection> Sections { get; }

    public ChartSeries ActiveSeries { get; } = Series.New();
    public ChartSeries ReadSeries { get; } = Series.New();
    public ChartSeries WriteSeries { get; } = Series.New();
    public ObservableCollection<VolumeVM> Volumes { get; } = new();
    public StatList Stats { get; } = new("Active time", "Read speed", "Write speed", "Response time", "Queue length", "Temperature", "Life remaining", "Data read", "Data written");

    [ObservableProperty] private string _activeText = "0%";
    [ObservableProperty] private string _readText = "0 B/s";
    [ObservableProperty] private string _writeText = "0 B/s";

    public void Update(DiskSample d)
    {
        ActiveSeries.Push(d.ActivePercent);
        ReadSeries.Push(d.ReadBps);
        WriteSeries.Push(d.WriteBps);
        ActiveText = $"{d.ActivePercent:0}%";
        ReadText = Fmt.Rate(d.ReadBps);
        WriteText = Fmt.Rate(d.WriteBps);

        Stats.Set("Active time", ActiveText);
        Stats.Set("Read speed", ReadText);
        Stats.Set("Write speed", WriteText);
        Stats.Set("Response time", $"{d.ResponseMs:0.0} ms");
        Stats.Set("Queue length", d.Queue.ToString("0"));
        Stats.Set("Temperature", Fmt.Temp(d.Temp));
        Stats.Set("Life remaining", double.IsNaN(d.LifePercent) ? null : $"{d.LifePercent:0}%");
        Stats.Set("Data read", double.IsNaN(d.DataReadGB) ? null : $"{d.DataReadGB:N0} GB");
        Stats.Set("Data written", double.IsNaN(d.DataWrittenGB) ? null : $"{d.DataWrittenGB:N0} GB");

        for (int i = 0; i < d.Volumes.Count; i++)
        {
            var v = d.Volumes[i];
            var vm = Volumes.FirstOrDefault(x => x.Letter == v.Letter);
            if (vm == null) { vm = new VolumeVM { Letter = v.Letter }; Volumes.Add(vm); }
            double used = v.Total > 0 ? (v.Total - v.Free) * 100.0 / v.Total : 0;
            vm.Label = string.IsNullOrWhiteSpace(v.Label) ? "Local Disk" : v.Label;
            vm.UsedPercent = used;
            vm.IsLow = used >= 90;
            vm.Detail = $"{Fmt.Bytes(v.Free)} free of {Fmt.Bytes(v.Total)} · {v.FileSystem}";
        }
    }
}

public sealed partial class StorageViewModel : PageViewModel
{
    public override string Title => "Storage";

    public ObservableCollection<DiskItem> Disks { get; } = new();

    public void Load(IEnumerable<DiskInfo> disks)
    {
        // Keep items (and their graph history) for drives that did not change when the list is re-read after a hot-plug.
        static string Key(DiskInfo d) => $"{d.Number}|{d.Model}|{d.SizeBytes}|{string.Join(",", d.Letters)}";
        var existing = Disks.ToDictionary(d => Key(d.Info));
        Replace(Disks, disks.Select(d => existing.TryGetValue(Key(d), out var item) ? item : new DiskItem(d)).ToList());
        ulong total = (ulong)Disks.Sum(d => (double)d.Info.SizeBytes);
        Subtitle = $"{Disks.Count} drive{(Disks.Count == 1 ? "" : "s")} · {Fmt.Bytes(total)} total";
        IsLoading = false;
    }

    public override void OnSnapshot(Snapshot s)
    {
        foreach (var d in s.Disks)
            Disks.FirstOrDefault(x => x.Info.Number == d.Number)?.Update(d);
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        foreach (var d in Disks)
        {
            foreach (var sec in d.Sections)
                yield return new InfoSection($"{d.Header} — {sec.Title}").Also(x => { foreach (var i in sec.Items) x.Add(i.Label, i.Value); });
            yield return d.Stats.ToSection($"{d.Header} — Live readings");
        }
    }
}
