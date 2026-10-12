using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class DeviceRow : ObservableObject
{
    public DeviceRow(DeviceEntry e) => Entry = e;
    public DeviceEntry Entry { get; }
    public string Name => Entry.Name;
    public string Manufacturer => Fmt.OrDash(Entry.Manufacturer);
    public string StatusText => Entry.HasProblem ? $"Problem (code {Entry.ErrorCode})" : Entry.Disabled ? "Disabled" : "Working";
    public bool HasProblem => Entry.HasProblem;
    public bool Disabled => Entry.Disabled;
    public string Tooltip => $"{Entry.DeviceId}\nDriver service: {Fmt.OrDash(Entry.Driver)}";
    [ObservableProperty] private bool _isVisible = true;

    [RelayCommand]
    private void CopyId() => Clip.Set(Entry.DeviceId);
}

public sealed partial class DeviceGroup : ObservableObject
{
    public DeviceGroup(string name, IEnumerable<DeviceRow> rows)
    {
        Name = name;
        Rows = new ObservableCollection<DeviceRow>(rows);
        Count = Rows.Count;
        Problems = Rows.Count(r => r.HasProblem);
    }

    public string Name { get; }
    public ObservableCollection<DeviceRow> Rows { get; }
    public int Problems { get; }
    public bool HasProblems => Problems > 0;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isExpanded;
}

public sealed partial class DevicesViewModel : PageViewModel
{
    public override string Title => "Devices";

    public ObservableCollection<DeviceGroup> Groups { get; } = new();
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _problemCount;
    private bool _loaded;

    protected override void OnActiveChanged(bool value)
    {
        if (value && !_loaded) _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        _loaded = true;
        IsLoading = true;
        var data = await Task.Run(SystemInfoReader.Devices);
        var groups = data.Select(d => new DeviceGroup(d.Class, d.Devices.Select(e => new DeviceRow(e)))).ToList();
        foreach (var g in groups.Where(g => g.HasProblems)) g.IsExpanded = true;
        Replace(Groups, groups.OrderByDescending(g => g.HasProblems).ThenBy(g => g.Name));
        Total = groups.Sum(g => g.Rows.Count);
        ProblemCount = groups.Sum(g => g.Problems);
        Subtitle = $"{Total} devices in {Groups.Count} categories" + (ProblemCount > 0 ? $" · {ProblemCount} with problems" : "");
        ApplyFilter();
        IsLoading = false;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        foreach (var g in Groups)
        {
            int visible = 0;
            bool groupMatch = f.Length > 0 && g.Name.Contains(f, StringComparison.OrdinalIgnoreCase);
            foreach (var r in g.Rows)
            {
                r.IsVisible = f.Length == 0 || groupMatch
                              || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                              || r.Manufacturer.Contains(f, StringComparison.OrdinalIgnoreCase)
                              || r.Entry.DeviceId.Contains(f, StringComparison.OrdinalIgnoreCase);
                if (r.IsVisible) visible++;
            }
            g.Count = visible;
            g.IsVisible = visible > 0;
            if (f.Length > 0) g.IsExpanded = visible > 0;
        }
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        foreach (var g in Groups)
        {
            var s = new InfoSection($"Devices — {g.Name}");
            foreach (var r in g.Rows) s.Add(r.Name, $"{r.Manufacturer} · {r.StatusText}");
            yield return s;
        }
    }
}
