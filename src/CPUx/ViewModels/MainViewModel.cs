using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CPUx.Core;
using CPUx.Services;

namespace CPUx.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public DashboardViewModel Dashboard { get; } = new();
    public CpuViewModel Cpu { get; } = new();
    public GpuViewModel Gpu { get; } = new();
    public MemoryViewModel Memory { get; } = new();
    public StorageViewModel Storage { get; } = new();
    public NetworkViewModel Network { get; } = new();
    public BoardViewModel Board { get; } = new();
    public BatteryViewModel Battery { get; } = new();
    public DisplayViewModel Displays { get; } = new();
    public SensorsViewModel Sensors { get; } = new();
    public ProcessesViewModel Processes { get; } = new();
    public DevicesViewModel Devices { get; } = new();
    public SystemViewModel Os { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public StressViewModel Stress { get; } = new();
    public FansViewModel Fans { get; } = new();

    public ObservableCollection<NavItem> Nav { get; } = new();
    public ICollectionView NavView { get; }

    [ObservableProperty] private NavItem? _selected;
    [ObservableProperty] private PageViewModel? _current;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isTopmost = AppSettings.Current.AlwaysOnTop;
    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private string _lastUpdate = "";

    public bool IsAdmin { get; } = SystemInfoReader.IsAdmin();
    /// <summary>Shows "Restart as administrator" (hidden when already elevated, or in a Store package that can't elevate).</summary>
    public bool CanElevate => !IsAdmin && PackagedApp.CanElevate;
    public string AdminText => IsAdmin ? "Administrator" : "Standard user";

    private readonly MonitorService _monitor;
    private Snapshot? _last;

    public event Action<Snapshot>? SnapshotApplied;
    public Snapshot? Last => _last;

    public MainViewModel(MonitorService monitor)
    {
        _monitor = monitor;
        Nav.Add(new NavItem("Dashboard", "I.Dashboard", "Overview", Dashboard));
        Nav.Add(new NavItem("Processor", "I.Cpu", "Hardware", Cpu));
        Nav.Add(new NavItem("Graphics", "I.Gpu", "Hardware", Gpu));
        Nav.Add(new NavItem("Memory", "I.Memory", "Hardware", Memory));
        Nav.Add(new NavItem("Storage", "I.Storage", "Hardware", Storage));
        Nav.Add(new NavItem("Network", "I.Network", "Hardware", Network));
        Nav.Add(new NavItem("Motherboard", "I.Board", "Hardware", Board));
        Nav.Add(new NavItem("Battery", "I.Battery", "Hardware", Battery));
        Nav.Add(new NavItem("Displays", "I.Display", "Hardware", Displays));
        Nav.Add(new NavItem("Sensors", "I.Thermometer", "System", Sensors));
        Nav.Add(new NavItem("Processes", "I.Activity", "System", Processes));
        Nav.Add(new NavItem("Devices", "I.Usb", "System", Devices));
        Nav.Add(new NavItem("Operating System", "I.Window", "System", Os));
        Nav.Add(new NavItem("Fan control", "I.Fan", "Tools", Fans));
        Nav.Add(new NavItem("Stress test", "I.Flame", "Tools", Stress));
        Nav.Add(new NavItem("Settings", "I.Settings", "Tools", Settings));

        NavView = new ListCollectionView(Nav);
        NavView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));
        Selected = Nav[0];

        _monitor.Updated += OnSnapshot;
    }

    private IEnumerable<PageViewModel> Pages => Nav.Select(n => n.Page);

    partial void OnSelectedChanged(NavItem? value)
    {
        if (value == null) return;
        if (Current != null) Current.IsActive = false;
        Current = value.Page;
        Current.IsActive = true;
    }

    partial void OnIsTopmostChanged(bool value)
    {
        AppSettings.Current.AlwaysOnTop = value;
        AppSettings.Current.Save();
    }

    [RelayCommand]
    private void Navigate(string? title)
    {
        // "Graphics#<luid>" opens the Graphics page on a specific adapter.
        string? detail = null;
        int hash = title?.IndexOf('#') ?? -1;
        if (hash > 0) { detail = title![(hash + 1)..]; title = title[..hash]; }
        var item = Nav.FirstOrDefault(n => n.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        Selected = item;
        if (detail != null && item.Page == Gpu)
            Gpu.Selected = Gpu.Gpus.FirstOrDefault(g => g.Info.LuidKey == detail) ?? Gpu.Selected;
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        _monitor.Paused = IsPaused;
        if (!IsPaused) _monitor.Wake();
        Status = IsPaused ? "Paused" : "Live";
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt.
        }
        catch (Exception ex)
        {
            Log.Error("Restart as admin", ex);
        }
    }

    [RelayCommand]
    private async Task Export(string? format)
    {
        await ReportExporter.ExportAsync(this, format ?? "html");
    }

    [RelayCommand]
    private void OpenMini() => ((App)Application.Current).ShowMiniMonitor();

    private void OnSnapshot(Snapshot s)
    {
        _last = s;
        foreach (var p in Pages)
        {
            try { p.OnSnapshot(s); }
            catch (Exception ex) { Log.Error($"Page {p.Title}", ex); }
        }
        CheckDiskChanges(s);
        LastUpdate = s.Time.ToString("HH:mm:ss");
        if (!IsPaused) Status = "Live";
        SnapshotApplied?.Invoke(s);
    }

    /// <summary>Loads static hardware inventory in parallel; each page fills in as soon as its data arrives.</summary>
    public async Task LoadInventoryAsync()
    {
        Status = "Reading hardware…";
        var cpuTask = Task.Run(CpuInfoReader.Read);
        var gpuTask = Task.Run(GpuInfoReader.Read);
        var memTask = Task.Run(SystemInfoReader.Memory);
        var diskTask = Task.Run(SystemInfoReader.Storage);
        var boardTask = Task.Run(() => { var l = SystemInfoReader.Board(out var s); return (l, s); });
        var osTask = Task.Run(() => { var l = SystemInfoReader.OperatingSystem(out var s); return (l, s); });
        var batteryTask = Task.Run(SystemInfoReader.Battery);

        await Run("CPU", cpuTask, cpu =>
        {
            Cpu.Load(cpu);
            _monitor.Cpu = cpu;
            Dashboard.CpuName = cpu.Name;
            Dashboard.SetSummary("Processor", $"{cpu.Name} ({cpu.Cores}C/{cpu.Threads}T)");
        });

        await Run("GPU", gpuTask, async gpus =>
        {
            Gpu.Load(gpus);
            _monitor.Gpus = gpus;
            Stress.SetHardware(_monitor.Cpu, gpus);
            Dashboard.SetGpus(gpus);
            Dashboard.SetSummary("Graphics", gpus.Count == 0 ? Fmt.Dash : string.Join(" + ", gpus.Select(g => g.ShortName)));
            var displays = await Task.Run(() => DisplayReader.Read(gpus));
            Displays.Load(displays);
            Dashboard.SetSummary("Display", displays.Count == 0 ? Fmt.Dash : string.Join(", ", displays.Select(d => $"{d.Width}×{d.Height} @ {d.RefreshHz:0} Hz")));
        });

        await Run("Memory", memTask, mem =>
        {
            Memory.Load(mem);
            Dashboard.SetSummary("Memory", mem.Summary);
        });

        await Run("Storage", diskTask, ApplyStorage);

        await Run("Board", boardTask, b =>
        {
            Board.Load(b.l, b.s);
            Dashboard.SetSummary("Device", b.s);
            var mb = b.l.FirstOrDefault(x => x.Title == "Motherboard");
            Dashboard.SetSummary("Motherboard", mb == null ? Fmt.Dash : $"{mb.Find("Manufacturer")?.Value} {mb.Find("Product")?.Value}".Trim());
        });

        await Run("OS", osTask, o =>
        {
            Os.Load(o.l, o.s);
            Dashboard.SetSummary("Operating system", o.s);
        });

        await Run("Battery", batteryTask, b =>
        {
            Battery.Load(b);
            Dashboard.SetBatteryHealth(b);
            if (!b.Present)
            {
                var nav = Nav.FirstOrDefault(n => n.Page == Battery);
                if (nav != null && Selected != nav) Nav.Remove(nav);
            }
        });
        Status = IsPaused ? "Paused" : "Live";
    }

    private void ApplyStorage(List<DiskInfo> disks)
    {
        _monitor.Disks = disks;
        Storage.Load(disks);
        Dashboard.SetDisks(disks);
        Stress.RefreshDrivesCommand.Execute(null);
        Dashboard.SetSummary("Storage", disks.Count == 0 ? Fmt.Dash :
            string.Join(", ", disks.Select(d => $"{Fmt.BytesDecimal(d.SizeBytes)} {(d.Media == "" ? d.Bus : d.Media)}")));
        _storageReady = true;
        _monitor.Wake();
    }

    // Drives that are plugged in or removed (USB drives, SD cards, external disks) show up as a change in the
    // disk counter instances; the storage inventory is then re-read so names, letters and volumes stay current.
    private bool _storageReady, _storageRefreshing, _storageRefreshPending;
    private string? _diskSignature;

    private void CheckDiskChanges(Snapshot s)
    {
        if (!_storageReady) return;
        string sig = string.Join("|", s.Disks.Select(d => d.Instance).OrderBy(x => x, StringComparer.Ordinal));
        if (sig == _diskSignature) return;
        bool first = _diskSignature == null;
        _diskSignature = sig;
        var known = _monitor.Disks.Select(d => d.Number).OrderBy(n => n);
        if (first && known.SequenceEqual(s.Disks.Select(d => d.Number).OrderBy(n => n))) return;
        _ = RefreshStorageAsync();
    }

    private async Task RefreshStorageAsync()
    {
        if (_storageRefreshing) { _storageRefreshPending = true; return; }
        _storageRefreshing = true;
        try
        {
            do
            {
                _storageRefreshPending = false;
                await Task.Delay(1500); // let Windows finish mounting the new volume
                var disks = await Task.Run(SystemInfoReader.Storage);
                Log.Write($"Drives changed: {string.Join(", ", disks.Select(d => $"{d.Number} {d.Model} {string.Join(" ", d.Letters)}"))}");
                ApplyStorage(disks);
            } while (_storageRefreshPending);
        }
        catch (Exception ex) { Log.Error("Storage refresh", ex); }
        finally { _storageRefreshing = false; }
    }

    private static async Task Run<T>(string what, Task<T> task, Action<T> apply)
    {
        try { apply(await task); }
        catch (Exception ex) { Log.Error("Inventory " + what, ex); }
    }

    private static async Task Run<T>(string what, Task<T> task, Func<T, Task> apply)
    {
        try { await apply(await task); }
        catch (Exception ex) { Log.Error("Inventory " + what, ex); }
    }

    public IEnumerable<(string Page, IEnumerable<InfoSection> Sections)> ReportPages() =>
        Pages.Where(p => p is not SettingsViewModel and not DashboardViewModel).Select(p => (p.Title, p.ReportSections()));
}
