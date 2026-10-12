using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CPUx.Core;
using CPUx.Services;
using Microsoft.Win32;

namespace CPUx.ViewModels;

public sealed partial class FanItem : ObservableObject
{
    private readonly FansViewModel _owner;
    private bool _syncing;
    private CancellationTokenSource? _debounce;

    public FanItem(FansViewModel owner, string id, string name, string group, bool canControl, string? note)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Group = group;
        CanControl = canControl;
        Note = note ?? (canControl ? "" : "Monitor only — this fan's speed can't be set from Windows.");
    }

    public string Id { get; }
    public string Name { get; }
    public string Group { get; }
    public bool CanControl { get; }
    public string Note { get; }
    public ChartSeries RpmSeries { get; } = Series.New();

    [ObservableProperty] private string _rpmText = Fmt.Dash;
    [ObservableProperty] private string _statusText = "Automatic";
    [ObservableProperty] private bool _isManual;
    [ObservableProperty] private double _target = 60;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _error = "";

    public string TargetText => $"{Target:0}%";

    partial void OnTargetChanged(double value)
    {
        OnPropertyChanged(nameof(TargetText));
        if (_syncing || !IsManual) return;
        // Apply once the slider settles instead of on every pixel of a drag.
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(250, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Application.Current.Dispatcher.Invoke(() => Apply()); }, TaskScheduler.Default);
    }

    partial void OnIsManualChanged(bool value)
    {
        if (_syncing) return;
        Apply();
    }

    /// <summary>Updates the switch and slider without sending anything to the hardware.</summary>
    public void Sync(bool manual, double? target = null)
    {
        _syncing = true;
        IsManual = manual;
        if (target is double t) Target = t;
        _syncing = false;
    }

    public void Apply() => _ = _owner.ApplyAsync(this, IsManual ? (int)Math.Round(Target) : null);

    public void Update(FanReading r)
    {
        RpmSeries.Push(double.IsNaN(r.Rpm) ? 0 : r.Rpm);
        RpmText = double.IsNaN(r.Rpm) ? Fmt.Dash : $"{r.Rpm:N0} RPM";
        // Only duty values the hardware itself reports are shown; the firmware's own curve position is an estimate.
        StatusText = IsManual ? $"Fixed at {Target:0}%"
            : !double.IsNaN(r.Percent) ? $"Automatic · {r.Percent:0}%" : "Automatic";
    }
}

public sealed partial class FansViewModel : PageViewModel
{
    public const double CpuSafetyC = 95, GpuSafetyC = 85;

    public override string Title => "Fan control";

    public ObservableCollection<FanItem> Fans { get; } = new();
    public ObservableCollection<string> Events { get; } = new();
    public AppSettings S => AppSettings.Current;

    [ObservableProperty] private bool _hasFans;
    [ObservableProperty] private bool _hasControllable;
    [ObservableProperty] private bool _isAsus;
    [ObservableProperty] private int _asusMode = -1;
    [ObservableProperty] private bool _armouryCrateRunning;
    [ObservableProperty] private string _hardwareText = "Looking for fans…";
    [ObservableProperty] private string _hintText = "";

    private readonly FanService _service = FanService.Instance;
    private bool _syncingMode;
    private Snapshot? _last;

    public FansViewModel()
    {
        Subtitle = "Set each fan's speed or leave it on automatic";
        _service.ChannelsChanged += () => Application.Current?.Dispatcher.BeginInvoke(Rebuild);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void Rebuild()
    {
        var keep = Fans.ToDictionary(f => f.Id);
        var list = new List<FanItem>();
        foreach (var ch in _service.Channels)
        {
            var item = keep.TryGetValue(ch.Id, out var existing) ? existing : new FanItem(this, ch.Id, ch.Name, ch.Group, ch.CanControl, ch.Note);
            item.Sync(ch.Manual != null, ch.Manual);
            list.Add(item);
        }
        // Fans the sensor library can read but not drive (most laptops, some boards) are listed for monitoring.
        list.AddRange(Fans.Where(f => f.Id.StartsWith("ro:", StringComparison.Ordinal)));
        Replace(Fans, list);

        IsAsus = _service.IsAsus;
        HasFans = Fans.Count > 0;
        HasControllable = Fans.Any(f => f.CanControl);
        _syncingMode = true;
        AsusMode = _service.AsusMode ?? -1;
        _syncingMode = false;
        ArmouryCrateRunning = IsAsus && Process.GetProcessesByName("ArmouryCrate.Service").Length + Process.GetProcessesByName("ArmouryCrate").Length > 0;

        int control = Fans.Count(f => f.CanControl);
        HardwareText = control == 0
            ? "No controllable fans found"
            : IsAsus ? $"{_service.AsusModel} · {control} fan{(control == 1 ? "" : "s")} controlled through the ASUS firmware"
            : $"{control} controllable fan{(control == 1 ? "" : "s")}";
        HintText = control > 0 ? ""
            : SystemInfoReader.IsAdmin() && HardwareSensors.IsPawnIoInstalled()
                ? "This PC's fans are managed by its firmware and don't accept speeds from Windows. Desktop motherboards with a supported Super I/O chip, most desktop graphics cards and ASUS laptops can be controlled."
                : "Desktop motherboard fans need CPUx running as administrator with the PawnIO driver installed. Laptops other than ASUS models keep fan control in their firmware.";
        IsLoading = false;
    }

    partial void OnAsusModeChanged(int value)
    {
        if (_syncingMode || value < 0) return;
        _ = Task.Run(() =>
        {
            bool ok = _service.SetAsusMode(value);
            Application.Current.Dispatcher.BeginInvoke(() => AddEvent(ok
                ? $"Performance mode set to {AsusAcpi.ModeName(value)}."
                : $"The firmware didn't accept {AsusAcpi.ModeName(value)} mode."));
        });
    }

    internal async Task ApplyAsync(FanItem item, int? percent)
    {
        item.IsBusy = true;
        item.Error = "";
        // The sensor library can be busy for a moment under heavy load, so hardware calls stay off the UI thread.
        bool ok = await Task.Run(() => percent is int p ? _service.SetManual(item.Id, p) : _service.SetAuto(item.Id));
        item.IsBusy = false;
        if (!ok)
        {
            item.Error = percent is int ? "The fan didn't accept the new speed." : "Couldn't return the fan to automatic.";
            item.Sync(_service.Channels.FirstOrDefault(c => c.Id == item.Id)?.Manual != null);
        }
        else
        {
            AddEvent(percent is int v ? $"{item.Name}: fixed at {v}%." : $"{item.Name}: automatic.");
        }
        if (_last != null) Update(item, _last);
    }

    [RelayCommand]
    private void AllAuto()
    {
        foreach (var f in Fans.Where(f => f.CanControl && f.IsManual)) f.IsManual = false;
    }

    [RelayCommand]
    private void AllMax()
    {
        foreach (var f in Fans.Where(f => f.CanControl))
        {
            f.Sync(true, 100);
            f.Apply();
        }
    }

    public override void OnSnapshot(Snapshot s)
    {
        _last = s;
        if (Fans.Count == 0 && _service.Channels.Count > 0) Rebuild();
        SyncReadOnly(s);
        foreach (var f in Fans) Update(f, s);
        CheckSafety(s);
    }

    private static void Update(FanItem f, Snapshot s)
    {
        if (f.Id.StartsWith("ro:", StringComparison.Ordinal))
        {
            double rpm = s.Sensors?.SelectMany(h => h.AllSensors).FirstOrDefault(x => x.Id == f.Id[3..])?.Value ?? double.NaN;
            f.Update(new FanReading { Id = f.Id, Rpm = rpm });
            return;
        }
        var r = s.Fans.FirstOrDefault(x => x.Id == f.Id);
        if (r != null) f.Update(r);
    }

    private void SyncReadOnly(Snapshot s)
    {
        if (s.Sensors == null) return;
        // Fan sensors that already belong to a controllable channel are shown on that channel instead.
        var driven = _service.Channels.Select(c => c.FanSensorId).Where(x => x != null).ToHashSet();
        foreach (var hw in s.Sensors.SelectMany(h => h.Children.Prepend(h)))
        {
            foreach (var sensor in hw.Sensors.Where(x => x.Type == "Fan" && !driven.Contains(x.Id)))
            {
                string id = "ro:" + sensor.Id;
                if (Fans.Any(f => f.Id == id)) continue;
                Fans.Add(new FanItem(this, id, sensor.Name, hw.Name, false, null));
                HasFans = true;
            }
        }
    }

    /// <summary>
    /// If a fan the user slowed down leaves the CPU or GPU at its safety temperature, it goes back to automatic.
    /// For ASUS fans "slowed down" means below the firmware's own curve at the current temperature.
    /// </summary>
    private void CheckSafety(Snapshot s)
    {
        if (!S.FanSafety) return;
        double cpu = s.Cpu.PackageTemp;
        double gpu = s.Gpus.Where(g => !double.IsNaN(g.Temp)).Select(g => g.Temp).DefaultIfEmpty(double.NaN).Max();
        bool cpuHot = cpu >= CpuSafetyC, gpuHot = gpu >= GpuSafetyC;
        if (!cpuHot && !gpuHot) return;

        foreach (var ch in _service.Channels.Where(c => c.CanControl && c.Manual is int))
        {
            bool hot = ch.Cools switch { "Cpu" => cpuHot, "Gpu" => gpuHot, _ => cpuHot || gpuHot };
            if (!hot) continue;
            var r = s.Fans.FirstOrDefault(x => x.Id == ch.Id);
            double floor = r != null && !double.IsNaN(r.AutoPercent) ? r.AutoPercent : 100;
            if (ch.Manual >= floor) continue;
            var item = Fans.FirstOrDefault(f => f.Id == ch.Id);
            if (item == null || item.IsBusy) continue;
            string what = ch.Cools == "Gpu" ? $"GPU at {Fmt.Temp(gpu)}" : $"CPU at {Fmt.Temp(cpuHot ? cpu : gpu)}";
            AddEvent($"{ch.Name} returned to automatic: {what} with the fan slowed down.");
            item.IsManual = false;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Sleep and AC/battery switches make ASUS firmware (and Armoury Crate) reload their own fan curves.
        if (e.Mode is not (PowerModes.Resume or PowerModes.StatusChange)) return;
        _ = Task.Delay(e.Mode == PowerModes.Resume ? 4000 : 2000).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _syncingMode = true;
            AsusMode = _service.AsusMode ?? -1;
            _syncingMode = false;
            var manual = Fans.Where(f => f.CanControl && f.IsManual).ToList();
            foreach (var f in manual) f.Apply();
            if (manual.Count > 0) AddEvent("Fan speeds re-applied after a power change.");
        }));
    }

    private void AddEvent(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (Events.Count > 50) Events.RemoveAt(Events.Count - 1);
    }

    [RelayCommand]
    private void OpenPawnIo()
    {
        try { Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true }); } catch { }
    }

    public override IEnumerable<InfoSection> ReportSections()
    {
        var sec = new InfoSection("Fans");
        foreach (var f in Fans) sec.Add($"{f.Group} — {f.Name}", $"{f.RpmText} · {f.StatusText}");
        if (sec.Items.Count > 0) yield return sec;
    }
}
