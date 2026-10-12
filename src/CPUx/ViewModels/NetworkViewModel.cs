using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class AdapterItem : ObservableObject
{
    public AdapterItem(string id) => Id = id;

    public string Id { get; }
    public ChartSeries RxSeries { get; } = Series.New();
    public ChartSeries TxSeries { get; } = Series.New();
    public InfoSection Details { get; } = new("Details");

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _type = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isUp;
    [ObservableProperty] private bool _isWireless;
    [ObservableProperty] private bool _isVirtual;
    [ObservableProperty] private string _rxText = "0 bps";
    [ObservableProperty] private string _txText = "0 bps";
    [ObservableProperty] private string _totals = "";
    [ObservableProperty] private string _speed = "";
    [ObservableProperty] private string _primaryAddress = "";

    private int _detailsHash;

    public void Update(NetSample n)
    {
        Name = n.Name;
        Description = n.Description;
        Type = n.Type;
        Status = n.Status;
        IsUp = n.IsUp;
        IsWireless = n.IsWireless;
        IsVirtual = n.IsVirtual;
        RxSeries.Push(n.RxBps);
        TxSeries.Push(n.TxBps);
        RxText = Fmt.Bits(n.RxBps);
        TxText = Fmt.Bits(n.TxBps);
        Totals = $"Received {Fmt.Bytes(n.RxTotal)} · Sent {Fmt.Bytes(n.TxTotal)}";
        Speed = n.Speed > 0 ? Fmt.LinkSpeed(n.Speed) : "";

        if (n.Details != null)
        {
            int hash = string.Join("|", n.Details.Select(d => d.Label + d.Value)).GetHashCode();
            if (hash != _detailsHash)
            {
                _detailsHash = hash;
                Details.Items.Clear();
                foreach (var (label, value) in n.Details) Details.Add(label, value);
                PrimaryAddress = n.Details.FirstOrDefault(d => d.Label == "IPv4 address").Value ?? "";
            }
        }
    }
}

public sealed partial class NetworkViewModel : PageViewModel
{
    public override string Title => "Network";

    public ObservableCollection<AdapterItem> Adapters { get; } = new();
    public ChartSeries TotalRx { get; } = Series.New();
    public ChartSeries TotalTx { get; } = Series.New();

    [ObservableProperty] private string _rxText = "0 bps";
    [ObservableProperty] private string _txText = "0 bps";
    [ObservableProperty] private string _hostName = Environment.MachineName;
    [ObservableProperty] private int _hiddenCount;

    private readonly Dictionary<string, AdapterItem> _all = new();

    public bool ShowAll
    {
        get => AppSettings.Current.ShowAllAdapters;
        set
        {
            if (AppSettings.Current.ShowAllAdapters == value) return;
            AppSettings.Current.ShowAllAdapters = value;
            AppSettings.Current.Save();
            OnPropertyChanged();
        }
    }

    public override void OnSnapshot(Snapshot s)
    {
        var physical = s.Networks.Where(n => n.IsUp && !n.IsVirtual).ToList();
        double rx = physical.Sum(n => n.RxBps), tx = physical.Sum(n => n.TxBps);
        TotalRx.Push(rx);
        TotalTx.Push(tx);
        RxText = Fmt.Bits(rx);
        TxText = Fmt.Bits(tx);

        foreach (var n in s.Networks)
        {
            if (!_all.TryGetValue(n.Id, out var item)) _all[n.Id] = item = new AdapterItem(n.Id);
            item.Update(n);
        }

        bool all = ShowAll;
        var visible = s.Networks
            .Where(n => all || (n.IsUp && !n.IsVirtual))
            .OrderByDescending(n => n.IsUp).ThenBy(n => n.IsVirtual).ThenByDescending(n => n.RxTotal + n.TxTotal)
            .Select(n => _all[n.Id]).ToList();
        HiddenCount = s.Networks.Count - visible.Count;
        if (!visible.SequenceEqual(Adapters)) Replace(Adapters, visible);

        int up = s.Networks.Count(n => n.IsUp && !n.IsVirtual);
        Subtitle = $"{up} active connection{(up == 1 ? "" : "s")} · {s.Networks.Count} adapters";
        IsLoading = false;
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        foreach (var a in _all.Values.Where(a => a.IsUp))
            yield return new InfoSection($"{a.Name}").Also(x => { foreach (var i in a.Details.Items) x.Add(i.Label, i.Value); });
    }
}
