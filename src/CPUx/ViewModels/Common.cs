using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isActive;

    public ObservableCollection<InfoSection> Sections { get; } = new();

    partial void OnIsActiveChanged(bool value) => OnActiveChanged(value);

    /// <summary>Called when the page becomes visible (true) or hidden (false).</summary>
    protected virtual void OnActiveChanged(bool active) { }

    public virtual void OnSnapshot(Snapshot s) { }

    /// <summary>Sections included in exported reports.</summary>
    public virtual IEnumerable<InfoSection> ReportSections() => Sections;

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}

/// <summary>A label/value pair displayed as a large statistic.</summary>
public sealed partial class Stat : ObservableObject
{
    public Stat(string label) => Label = label;
    public string Label { get; }
    [ObservableProperty] private string _value = Fmt.Dash;
}

/// <summary>Ordered collection of stats. Stats appear the first time they receive a real value and keep their slot.</summary>
public sealed class StatList : ObservableCollection<Stat>
{
    private readonly List<string> _order;
    private readonly Dictionary<string, Stat> _map = new();

    public StatList(params string[] order) => _order = order.ToList();

    public void Set(string label, string? value)
    {
        bool meaningful = !string.IsNullOrWhiteSpace(value) && value != Fmt.Dash;
        if (_map.TryGetValue(label, out var stat))
        {
            stat.Value = meaningful ? value! : Fmt.Dash;
            return;
        }
        if (!meaningful) return;
        stat = new Stat(label) { Value = value! };
        _map[label] = stat;
        int rank = _order.IndexOf(label);
        if (rank < 0) { _order.Add(label); rank = _order.Count - 1; }
        int index = 0;
        while (index < Count && _order.IndexOf(this[index].Label) < rank) index++;
        Insert(index, stat);
    }

    public InfoSection ToSection(string title)
    {
        var s = new InfoSection(title);
        foreach (var st in this) s.Add(st.Label, st.Value);
        return s;
    }
}

public sealed class NavItem
{
    public NavItem(string title, string icon, string group, PageViewModel page)
    {
        Title = title;
        Icon = icon;
        Group = group;
        Page = page;
    }

    public string Title { get; }
    public string Icon { get; }
    public string Group { get; }
    public PageViewModel Page { get; }
}

/// <summary>Creates chart series sized from settings and resizes them all when the history length changes.</summary>
public static class Series
{
    private static readonly List<WeakReference<ChartSeries>> All = new();

    public static ChartSeries New()
    {
        var s = new ChartSeries(AppSettings.Current.HistoryPoints);
        All.Add(new WeakReference<ChartSeries>(s));
        return s;
    }

    public static void ResizeAll()
    {
        int points = AppSettings.Current.HistoryPoints;
        All.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in All)
            if (w.TryGetTarget(out var s)) s.Resize(points);
    }
}
