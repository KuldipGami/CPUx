using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class ProcessItem : ObservableObject
{
    public ProcessItem(int pid, string name, DateTime started)
    {
        Pid = pid;
        Name = name;
        Started = started;
        Description = name;
    }

    public int Pid { get; }
    public string Name { get; }
    public DateTime Started { get; }
    public string StartedText => Started == DateTime.MinValue ? Fmt.Dash : Started.ToString("yyyy-MM-dd HH:mm:ss");

    [ObservableProperty] private string _description;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private long _memory;
    [ObservableProperty] private double _disk;
    [ObservableProperty] private double _gpu;
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _handles;
    [ObservableProperty] private int _session;
    [ObservableProperty] private string _cpuText = "0%";
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private string _gpuText = "";
    [ObservableProperty] private long _commit;
    [ObservableProperty] private string _commitText = "";
}

public sealed partial class ProcessesViewModel : PageViewModel
{
    public override string Title => "Processes";

    public ObservableCollection<ProcessItem> Processes { get; } = new();
    public ListCollectionView View { get; }

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private ProcessItem? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _totalCpu = "";
    [ObservableProperty] private string _totalMemory = "";

    private readonly Dictionary<int, ProcessItem> _map = new();
    private List<ProcSample>? _pending;
    private Dictionary<int, double>? _pendingGpu;
    private static readonly ConcurrentDictionary<string, (ImageSource? Icon, string Description)> FileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ProcessItem> _resolveQueue = new();
    private int _resolving;

    public ProcessesViewModel()
    {
        View = new ListCollectionView(Processes) { IsLiveSorting = true, Filter = FilterItem };
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.Cpu), ListSortDirection.Descending));
        View.LiveSortingProperties.Add(nameof(ProcessItem.Cpu));
        ((INotifyCollectionChanged)View.SortDescriptions).CollectionChanged += (_, _) =>
        {
            View.LiveSortingProperties.Clear();
            foreach (var sd in View.SortDescriptions) View.LiveSortingProperties.Add(sd.PropertyName);
        };
        Subtitle = "Running applications and background processes";
    }

    private bool FilterItem(object o)
    {
        if (string.IsNullOrWhiteSpace(Filter)) return true;
        var p = (ProcessItem)o;
        string f = Filter.Trim();
        return p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
               || p.Description.Contains(f, StringComparison.OrdinalIgnoreCase)
               || p.Pid.ToString() == f;
    }

    partial void OnFilterChanged(string value) => View.Refresh();

    protected override void OnActiveChanged(bool value)
    {
        if (value && _pending != null) Apply(_pending, _pendingGpu ?? new());
    }

    public override void OnSnapshot(Snapshot s)
    {
        if (s.Processes == null) return;
        _pending = s.Processes;
        _pendingGpu = s.GpuByPid;
        var all = s.Processes;
        Summary = $"{all.Count} processes · {all.Sum(p => p.Threads):N0} threads · {all.Sum(p => p.Handles):N0} handles";
        TotalCpu = $"{s.Cpu.Usage:0}%";
        TotalMemory = $"{s.Memory.Load:0}%";
        if (IsActive) Apply(all, s.GpuByPid);
    }

    private void Apply(List<ProcSample> samples, Dictionary<int, double> gpu)
    {
        var seen = new HashSet<int>();
        foreach (var p in samples)
        {
            seen.Add(p.Pid);
            if (!_map.TryGetValue(p.Pid, out var item) || item.Started != p.Started)
            {
                if (item != null) Processes.Remove(item);
                item = new ProcessItem(p.Pid, p.Name, p.Started);
                _map[p.Pid] = item;
                Processes.Add(item);
                _resolveQueue.Enqueue(item);
            }
            item.Cpu = Math.Round(p.Cpu, 1);
            item.CpuText = p.Cpu < 0.05 ? "0%" : $"{p.Cpu:0.0}%";
            item.Memory = p.PrivateWs;
            item.MemoryText = Fmt.Bytes(p.PrivateWs);
            item.Commit = p.Commit;
            item.CommitText = Fmt.Bytes(p.Commit);
            item.Disk = p.IoBps;
            item.DiskText = p.IoBps < 1 ? "0 B/s" : Fmt.Rate(p.IoBps);
            double g = gpu.GetValueOrDefault(p.Pid);
            item.Gpu = Math.Round(g, 1);
            item.GpuText = g < 0.05 ? "" : $"{g:0.0}%";
            item.Threads = p.Threads;
            item.Handles = p.Handles;
            item.Session = p.Session;
        }
        foreach (var pid in _map.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Processes.Remove(_map[pid]);
            _map.Remove(pid);
        }
        IsLoading = false;
        StartResolver();
    }

    private void StartResolver()
    {
        if (_resolveQueue.IsEmpty || Interlocked.Exchange(ref _resolving, 1) == 1) return;
        var ui = Application.Current.Dispatcher;
        Task.Run(() =>
        {
            try
            {
                while (_resolveQueue.TryDequeue(out var item))
                {
                    string? path = item.Pid <= 4 ? null : Native.ProcessPath(item.Pid);
                    if (path == null) continue;
                    var info = FileCache.GetOrAdd(path, Resolve);
                    ui.BeginInvoke(() =>
                    {
                        item.Path = path;
                        item.Icon = info.Icon;
                        if (!string.IsNullOrWhiteSpace(info.Description)) item.Description = info.Description;
                    });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _resolving, 0);
            }
        });
    }

    private static (ImageSource? Icon, string Description) Resolve(string path)
    {
        ImageSource? icon = null;
        string desc = "";
        try
        {
            var fi = new Native.SHFILEINFO();
            IntPtr r = Native.SHGetFileInfo(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_ICON | Native.SHGFI_SMALLICON);
            if (r != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                icon = src;
                Native.DestroyIcon(fi.hIcon);
            }
        }
        catch { }
        try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
        return (icon, desc);
    }

    [RelayCommand]
    private void EndTask(ProcessItem? item)
    {
        item ??= Selected;
        if (item == null) return;
        var answer = MessageBox.Show(
            $"End \"{item.Description}\" (PID {item.Pid})?\n\nUnsaved data in this process will be lost.",
            "End task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            using var p = Process.GetProcessById(item.Pid);
            p.Kill();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't end the process: {ex.Message}", "End task", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenLocation(ProcessItem? item)
    {
        item ??= Selected;
        if (item?.Path == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); }
        catch (Exception ex) { Log.Error("Open location", ex); }
    }

    [RelayCommand]
    private void CopyDetails(ProcessItem? item)
    {
        item ??= Selected;
        if (item == null) return;
        Clip.Set($"{item.Description}\nName: {item.Name}\nPID: {item.Pid}\nPath: {item.Path}\nCPU: {item.CpuText}\nMemory: {item.MemoryText}\nThreads: {item.Threads}\nHandles: {item.Handles}\nStarted: {item.StartedText}");
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        var s = new InfoSection("Top processes by memory");
        foreach (var p in Processes.OrderByDescending(p => p.Memory).Take(25))
            s.Add($"{p.Name} ({p.Pid})", $"{p.MemoryText} · CPU {p.CpuText}");
        return new[] { s };
    }
}
