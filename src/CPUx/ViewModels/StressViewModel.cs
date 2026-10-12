using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CPUx.Core;
using CPUx.Services;
using CPUx.Services.Stress;

namespace CPUx.ViewModels;

public sealed partial class StressCard : ObservableObject
{
    public StressCard(string title, bool enabled)
    {
        Title = title;
        _enabled = enabled;
    }

    public string Title { get; }
    public ChartSeries ScoreSeries { get; } = Series.New();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _state = "Idle";
    [ObservableProperty] private string _score = Fmt.Dash;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private long _errors;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _average = "";

    public void Reset()
    {
        State = Enabled ? "Starting" : "Idle";
        Score = Fmt.Dash;
        Detail = "";
        Errors = 0;
        ErrorText = "";
        Average = "";
        ScoreSeries.Clear();
    }
}

public sealed record DriveOption(string Root, string Label);

public sealed record StressLogEntry(string Time, string Text, string Kind);

public sealed partial class StressViewModel : PageViewModel
{
    public override string Title => "Stress test";

    public StressCard Cpu { get; } = new("Processor", true);
    public StressCard Gpu { get; } = new("Graphics", false);
    public StressCard Memory { get; } = new("Memory", false);
    public StressCard Disk { get; } = new("Storage", false);

    public ObservableCollection<GpuInfo> Gpus { get; } = new();
    public ObservableCollection<DriveOption> Drives { get; } = new();
    public ObservableCollection<StressLogEntry> Log { get; } = new();
    public ObservableCollection<InfoSection> Results { get; } = new();

    // Options
    [ObservableProperty] private string _cpuMode = "Mixed";
    [ObservableProperty] private string _cpuThreads = "All";
    [ObservableProperty] private GpuInfo? _selectedGpu;
    [ObservableProperty] private bool _gpuMemory = true;
    [ObservableProperty] private int _memPercent = 70;
    [ObservableProperty] private DriveOption? _selectedDrive;
    [ObservableProperty] private int _diskGb = 4;
    [ObservableProperty] private int _durationMin = 10;
    [ObservableProperty] private int _cpuTempLimit = 95;
    [ObservableProperty] private int _gpuTempLimit = 87;
    [ObservableProperty] private bool _stopOnError = true;

    // State
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private bool _isStopping;
    [ObservableProperty] private string _state = "Ready";
    [ObservableProperty] private string _timeText = "00:00:00";
    [ObservableProperty] private string _goalText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Choose components, then start the test.";
    [ObservableProperty] private bool _onBattery;
    [ObservableProperty] private bool _cpuTempMissing;
    [ObservableProperty] private string _allThreadsLabel = $"All ({Environment.ProcessorCount})";
    [ObservableProperty] private string _physicalLabel = "Physical cores";
    [ObservableProperty] private string _freeMemoryText = "";

    // Live monitor
    public ChartSeries CpuTemp { get; } = Series.New();
    public ChartSeries GpuTemp { get; } = Series.New();
    public ChartSeries CpuUsage { get; } = Series.New();
    public ChartSeries GpuUsage { get; } = Series.New();
    public ChartSeries CpuClock { get; } = Series.New();
    public ChartSeries GpuClock { get; } = Series.New();
    public ChartSeries CpuPower { get; } = Series.New();
    public ChartSeries GpuPower { get; } = Series.New();
    [ObservableProperty] private string _cpuTempText = Fmt.Dash;
    [ObservableProperty] private string _gpuTempText = Fmt.Dash;
    [ObservableProperty] private string _cpuUsageText = Fmt.Dash;
    [ObservableProperty] private string _gpuUsageText = Fmt.Dash;
    [ObservableProperty] private string _cpuClockText = Fmt.Dash;
    [ObservableProperty] private string _gpuClockText = Fmt.Dash;
    [ObservableProperty] private string _cpuPowerText = Fmt.Dash;
    [ObservableProperty] private string _gpuPowerText = Fmt.Dash;

    private readonly Dictionary<StressCard, StressComponent> _components = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastSnapshot = DateTime.UtcNow;
    private double _lastCpuTemp = double.NaN, _lastGpuTemp = double.NaN;
    private bool _warnedStale;
    private readonly Stopwatch _clock = new();
    private DateTime _startedAt;
    private string? _stopReason;
    private int _cores = Environment.ProcessorCount;

    // Run statistics
    private double _peakCpuTemp, _peakGpuTemp, _peakCpuPower, _peakGpuPower, _minCpuClock, _minGpuClock;
    private readonly List<double> _cpuClockSamples = new(), _gpuClockSamples = new();
    private double _cpuUsageSum;
    private int _samples;

    public StressViewModel()
    {
        Subtitle = "Push CPU, GPU, memory and storage to full load and check they stay stable";
        IsLoading = false;
        _timer.Tick += OnTimer;
        RefreshDrives();
    }

    public void SetHardware(CpuInfo? cpu, IEnumerable<GpuInfo> gpus)
    {
        if (cpu != null)
        {
            _cores = Math.Max(1, cpu.Cores);
            PhysicalLabel = $"Physical cores ({_cores})";
        }
        Replace(Gpus, gpus);
        SelectedGpu ??= Gpus.FirstOrDefault();
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        var current = SelectedDrive?.Root;
        var list = new List<DriveOption>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                list.Add(new DriveOption(d.Name, $"{d.Name.TrimEnd('\\')} {label} · {Fmt.Bytes(d.AvailableFreeSpace)} free"));
            }
            catch { }
        }
        Replace(Drives, list);
        SelectedDrive = Drives.FirstOrDefault(d => d.Root == current) ?? Drives.FirstOrDefault();
    }

    private void AddLog(string text, string kind = "info")
    {
        Log.Insert(0, new StressLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, kind));
        while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
    }

    // ------------------------------------------------------------------ start / stop
    [RelayCommand]
    private void Start()
    {
        if (IsRunning) return;
        if (!Cpu.Enabled && !Gpu.Enabled && !Memory.Enabled && !Disk.Enabled)
        {
            StatusText = "Turn on at least one component.";
            return;
        }
        if (Gpu.Enabled && SelectedGpu == null) { StatusText = "Pick a graphics adapter."; return; }
        if (Disk.Enabled && SelectedDrive == null) { StatusText = "Pick a drive for the storage test."; return; }

        _components.Clear();
        foreach (var card in new[] { Cpu, Gpu, Memory, Disk }) card.Reset();
        try
        {
            if (Cpu.Enabled)
            {
                int threads = CpuThreads switch { "Single" => 1, "Physical" => _cores, _ => Environment.ProcessorCount };
                var mode = CpuMode switch { "Fma" => CpuStress.Mode.Fma, "Integer" => CpuStress.Mode.Integer, _ => CpuStress.Mode.Mixed };
                _components[Cpu] = new CpuStress(threads, mode);
            }
            if (Gpu.Enabled) _components[Gpu] = new GpuStress(SelectedGpu!, GpuMemory);
            if (Memory.Enabled) _components[Memory] = new MemoryStress(MemPercent, Math.Clamp(Environment.ProcessorCount / 4, 2, 8));
            if (Disk.Enabled) _components[Disk] = new DiskStress(SelectedDrive!.Root, DiskGb * 1024L * 1024 * 1024);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't start: {ex.Message}";
            return;
        }

        ResetStats();
        _stopReason = null;
        Native.DisableProcessThrottling();
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; // keep the UI responsive while every core is busy
        _startedAt = DateTime.Now;
        _clock.Restart();
        _warnedStale = false;
        _lastSnapshot = DateTime.UtcNow;
        _timer.Start();
        IsRunning = true;
        IsIdle = false;
        State = "Running";
        Progress = 0;
        GoalText = DurationMin > 0 ? $"of {Fmt.Duration(TimeSpan.FromMinutes(DurationMin))}" : "until stopped";
        StatusText = $"Safety stop at {Fmt.Temp(CpuTempLimit)} CPU / {Fmt.Temp(GpuTempLimit)} GPU" + (StopOnError ? " or on the first error." : ".");
        Results.Clear();
        AddLog($"Stress test started: {string.Join(", ", _components.Keys.Select(k => k.Title))}");

        var ui = Application.Current.Dispatcher;
        foreach (var (card, comp) in _components)
        {
            comp.Message += m => ui.BeginInvoke(() => AddLog(m, m.Contains("mismatch") || m.Contains("wrong") || m.Contains("corrupt") || m.Contains("failed") || m.Contains("stopped responding") ? "error" : "info"));
            try { comp.Start(); }
            catch (Exception ex)
            {
                card.State = "Failed";
                AddLog($"{comp.Name}: couldn't start — {ex.Message}", "error");
            }
        }
    }

    [RelayCommand]
    private void Stop() => _ = StopAsync("Stopped by you");

    private async Task StopAsync(string reason)
    {
        if (!IsRunning || IsStopping) return;
        _stopReason = reason;
        IsStopping = true;
        State = "Stopping";
        StatusText = "Stopping workers and releasing memory…";
        var comps = _components.ToList();
        foreach (var (card, comp) in comps) card.Average = AverageOf(comp);
        _timer.Stop();
        _clock.Stop();
        await Task.Run(() => Parallel.ForEach(comps, c => c.Value.Stop()));
        foreach (var (card, comp) in comps)
        {
            card.State = comp.Status == "Failed" ? "Failed" : comp.Errors > 0 ? "Errors" : "Passed";
            card.Errors = comp.Errors;
        }
        IsStopping = false;
        IsRunning = false;
        IsIdle = true;
        Thread.CurrentThread.Priority = ThreadPriority.Normal;

        long errors = comps.Sum(c => c.Value.Errors);
        bool failed = errors > 0 || comps.Any(c => c.Value.Status == "Failed");
        bool early = DurationMin > 0 && _clock.Elapsed.TotalMinutes < DurationMin && !reason.StartsWith("Duration");
        State = failed ? "Failed" : reason.StartsWith("Safety") || early ? "Stopped" : "Passed";
        StatusText = failed
            ? $"{errors} error{(errors == 1 ? "" : "s")} detected — {reason.ToLowerInvariant()}."
            : reason.StartsWith("Safety") ? reason
            : early ? $"No errors in {Fmt.ShortDuration(_clock.Elapsed)}, but the test was stopped before the planned {DurationMin} min."
            : $"No errors in {Fmt.ShortDuration(_clock.Elapsed)}. {reason}.";
        AddLog($"Stress test finished: {reason} after {Fmt.Duration(_clock.Elapsed)}", failed ? "error" : "info");
        BuildResults(comps, failed, errors);
        RefreshDrives();
    }

    /// <summary>Synchronous stop used when the app exits.</summary>
    public void Shutdown()
    {
        foreach (var c in _components.Values)
        {
            try { c.Stop(); } catch { }
        }
    }

    private static string AverageOf(StressComponent c) => c switch
    {
        CpuStress cpu => cpu.AverageScore(),
        GpuStress gpu => gpu.AverageScore(),
        MemoryStress mem => mem.AverageScore(),
        DiskStress disk => disk.AverageScore(),
        _ => "",
    };

    // ------------------------------------------------------------------ live updates
    public override void OnSnapshot(Snapshot s)
    {
        var c = s.Cpu;
        var g = (SelectedGpu == null ? null : s.Gpus.FirstOrDefault(x => x.Luid == SelectedGpu.LuidKey)) ?? s.Gpus.FirstOrDefault();

        CpuTemp.Push(Nz(c.PackageTemp));
        GpuTemp.Push(Nz(g?.Temp ?? double.NaN));
        CpuUsage.Push(c.Usage);
        GpuUsage.Push(g?.Usage ?? 0);
        CpuClock.Push(Nz(c.MHz));
        GpuClock.Push(Nz(g?.CoreMHz ?? double.NaN));
        CpuPower.Push(Nz(c.PackagePower));
        GpuPower.Push(Nz(g?.Power ?? double.NaN));
        CpuTempText = Fmt.Temp(c.PackageTemp);
        GpuTempText = Fmt.Temp(g?.Temp ?? double.NaN);
        CpuUsageText = $"{c.Usage:0}%";
        GpuUsageText = g == null ? Fmt.Dash : $"{g.Usage:0}%";
        CpuClockText = Fmt.MHz(c.MHz);
        GpuClockText = Fmt.MHz(g?.CoreMHz ?? double.NaN);
        CpuPowerText = Fmt.Watts(c.PackagePower);
        GpuPowerText = Fmt.Watts(g?.Power ?? double.NaN);
        CpuTempMissing = double.IsNaN(c.PackageTemp);
        OnBattery = s.Battery is { OnAc: false };
        var mem = Native.MemoryStatus();
        FreeMemoryText = $"{Fmt.Bytes(mem.ullAvailPhys)} free now";

        _lastSnapshot = DateTime.UtcNow;
        _lastCpuTemp = c.PackageTemp;
        _lastGpuTemp = Gpu.Enabled && g != null ? g.Temp : double.NaN;
        if (!IsRunning || IsStopping) return;

        // Statistics
        _samples++;
        _cpuUsageSum += double.IsNaN(c.BusyPercent) ? c.Usage : c.BusyPercent;
        if (!double.IsNaN(c.PackageTemp)) _peakCpuTemp = Math.Max(_peakCpuTemp, c.PackageTemp);
        if (!double.IsNaN(c.PackagePower)) _peakCpuPower = Math.Max(_peakCpuPower, c.PackagePower);
        if (g != null && !double.IsNaN(g.Temp)) _peakGpuTemp = Math.Max(_peakGpuTemp, g.Temp);
        if (g != null && !double.IsNaN(g.Power)) _peakGpuPower = Math.Max(_peakGpuPower, g.Power);
        if (_clock.Elapsed.TotalSeconds > 5)
        {
            if (Cpu.Enabled && !double.IsNaN(c.MHz)) { _cpuClockSamples.Add(c.MHz); _minCpuClock = _minCpuClock == 0 ? c.MHz : Math.Min(_minCpuClock, c.MHz); }
            if (Gpu.Enabled && g != null && !double.IsNaN(g.CoreMHz)) { _gpuClockSamples.Add(g.CoreMHz); _minGpuClock = _minGpuClock == 0 ? g.CoreMHz : Math.Min(_minGpuClock, g.CoreMHz); }
        }
        CheckSafety();
    }

    /// <summary>
    /// Runs on its own timer so the test clock, duration and error checks keep working even if
    /// hardware sampling is slow while the machine is saturated.
    /// </summary>
    private void OnTimer(object? sender, EventArgs e)
    {
        if (!IsRunning || IsStopping) return;
        var elapsed = _clock.Elapsed;
        TimeText = Fmt.Duration(elapsed);
        if (DurationMin > 0) Progress = Math.Min(100, elapsed.TotalMinutes / DurationMin * 100);

        foreach (var (card, comp) in _components)
        {
            var r = comp.Read();
            card.Score = r.Score;
            card.Detail = r.Detail;
            card.State = r.Status;
            card.Errors = r.Errors;
            card.ErrorText = r.Errors > 0 ? comp.FirstError ?? "" : "";
            card.ScoreSeries.Push(r.ScoreValue);
        }

        double staleFor = (DateTime.UtcNow - _lastSnapshot).TotalSeconds;
        if (staleFor > 10 && !_warnedStale)
        {
            _warnedStale = true;
            AddLog($"Live readings are {staleFor:0} s behind because the system is saturated; temperature limits act on the latest reading.", "error");
        }
        CheckSafety();
    }

    private void CheckSafety()
    {
        if (!IsRunning || IsStopping) return;
        if (!double.IsNaN(_lastCpuTemp) && _lastCpuTemp >= CpuTempLimit)
            _ = StopAsync($"Safety stop: CPU reached {Fmt.Temp(_lastCpuTemp)} (limit {Fmt.Temp(CpuTempLimit)})");
        else if (!double.IsNaN(_lastGpuTemp) && _lastGpuTemp >= GpuTempLimit)
            _ = StopAsync($"Safety stop: GPU reached {Fmt.Temp(_lastGpuTemp)} (limit {Fmt.Temp(GpuTempLimit)})");
        else if (StopOnError && _components.Values.Any(x => x.Errors > 0))
            _ = StopAsync("Stopped on the first error");
        else if (_components.Count > 0 && _components.Values.All(x => x.Status == "Failed"))
            _ = StopAsync("Every component failed");
        else if (DurationMin > 0 && _clock.Elapsed.TotalMinutes >= DurationMin)
            _ = StopAsync("Duration reached");
    }
    private static double Nz(double v) => double.IsNaN(v) ? 0 : v;

    private void ResetStats()
    {
        _peakCpuTemp = _peakGpuTemp = _peakCpuPower = _peakGpuPower = _minCpuClock = _minGpuClock = 0;
        _cpuClockSamples.Clear();
        _gpuClockSamples.Clear();
        _cpuUsageSum = 0;
        _samples = 0;
    }

    private static string ClockTrend(List<double> samples, double min)
    {
        if (samples.Count < 10) return Fmt.Dash;
        int n = Math.Max(3, samples.Count / 6);
        double first = samples.Take(n).Average(), last = samples.Skip(samples.Count - n).Average();
        double drop = first > 0 ? (1 - last / first) * 100 : 0;
        string trend = drop >= 5 ? $"dropped {drop:0}% — thermal or power throttling" : "held steady";
        return $"{Fmt.MHz(first)} → {Fmt.MHz(last)} ({trend}), lowest {Fmt.MHz(min)}";
    }

    private void BuildResults(List<KeyValuePair<StressCard, StressComponent>> comps, bool failed, long errors)
    {
        var summary = new InfoSection("Result");
        summary.Add("Outcome", failed ? $"Failed — {errors} error{(errors == 1 ? "" : "s")}"
                             : _stopReason?.StartsWith("Safety") == true ? "Stopped by safety limit"
                             : State == "Stopped" ? "No errors (stopped early)" : "Passed — no errors")
               .Add("Reason", _stopReason)
               .Add("Started", _startedAt.ToString("yyyy-MM-dd HH:mm:ss"))
               .Add("Duration", Fmt.Duration(_clock.Elapsed))
               .Add("Components", string.Join(", ", comps.Select(c => c.Key.Title)));
        Results.Add(summary);

        var perf = new InfoSection("Performance");
        foreach (var (card, comp) in comps)
            perf.Add(card.Title, string.IsNullOrEmpty(card.Average) ? Fmt.Dash : card.Average);
        foreach (var (card, comp) in comps.Where(c => c.Value.Errors > 0))
            perf.Add($"{card.Title} errors", $"{comp.Errors} — {comp.FirstError}");
        Results.Add(perf);

        var thermal = new InfoSection("Temperatures, power and clocks");
        thermal.Add("Peak CPU temperature", _peakCpuTemp > 0 ? Fmt.Temp(_peakCpuTemp) : "Unavailable (needs admin + PawnIO)")
               .AddIf("Peak GPU temperature", _peakGpuTemp > 0 ? Fmt.Temp(_peakGpuTemp) : null)
               .AddIf("Peak CPU power", _peakCpuPower > 0 ? Fmt.Watts(_peakCpuPower) : null)
               .AddIf("Peak GPU power", _peakGpuPower > 0 ? Fmt.Watts(_peakGpuPower) : null)
               .AddIf("Average CPU busy time", _samples > 0 ? $"{_cpuUsageSum / _samples:0}%" : null)
               .AddIf("CPU clock", Cpu.Enabled ? ClockTrend(_cpuClockSamples, _minCpuClock) : null)
               .AddIf("GPU clock", Gpu.Enabled ? ClockTrend(_gpuClockSamples, _minGpuClock) : null);
        Results.Add(thermal);
    }

    public override IEnumerable<InfoSection> ReportSections() =>
        Results.Select(r => new InfoSection($"Last stress test — {r.Title}").Also(x => { foreach (var i in r.Items) x.Add(i.Label, i.Value); }));
}
