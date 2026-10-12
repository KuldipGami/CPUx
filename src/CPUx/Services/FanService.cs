using CPUx.Core;

namespace CPUx.Services;

/// <summary>Live reading for one fan, published in every snapshot.</summary>
public sealed class FanReading
{
    public string Id = "";
    public double Rpm = double.NaN;
    /// <summary>Duty the fan is running at, when the hardware reports it.</summary>
    public double Percent = double.NaN;
    /// <summary>Duty the automatic (firmware) curve would use right now; NaN when unknown.</summary>
    public double AutoPercent = double.NaN;
}

/// <summary>A fan CPUx knows about. Controllable fans accept a fixed speed or can be handed back to automatic control.</summary>
public sealed class FanChannel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; }
    public bool CanControl { get; init; }
    /// <summary>Which temperature this fan mainly cools ("Cpu", "Gpu" or "" for case/system fans); used by the safety check.</summary>
    public string Cools { get; init; } = "";
    public string? Note { get; init; }

    /// <summary>Fixed speed currently applied by CPUx, or null when the fan is on automatic control.</summary>
    public int? Manual { get; internal set; }

    internal uint AsusFan, AsusCurve;
    internal Dictionary<int, FanCurve>? AsusFactory;
    internal string? LhmControlId, LhmFanId;

    /// <summary>LibreHardwareMonitor RPM sensor that belongs to this fan, if any.</summary>
    public string? FanSensorId => LhmFanId;
}

/// <summary>
/// Finds controllable fans and applies manual speeds. Two back ends:
/// ASUS laptops through the firmware's fan curves (no admin needed), and LibreHardwareMonitor for desktop
/// motherboards (Super I/O, needs admin + PawnIO) and GPU coolers. Every manual change is undone when CPUx exits.
/// </summary>
public sealed class FanService
{
    private readonly object _gate = new();
    private List<FanChannel> _channels = new();
    private bool _asusTouched;
    private int? _asusMode;

    public static FanService Instance { get; } = new();

    public IReadOnlyList<FanChannel> Channels => _channels;
    public bool IsAsus { get; private set; }
    public string AsusModel { get; private set; } = "";
    public event Action? ChannelsChanged;

    /// <summary>The ASUS performance mode in effect: set by CPUx, otherwise Armoury Crate's last choice.</summary>
    public int? AsusMode => _asusMode ?? AsusAcpi.ArmouryCrateMode(OnAc());

    private static bool OnAc() => !Native.GetSystemPowerStatus(out var p) || p.ACLineStatus != 0;

    /// <summary>Discovers fans. Safe to call again (e.g. after the sensor library opens with admin rights).</summary>
    public void Discover(HardwareSensors? sensors)
    {
        var list = new List<FanChannel>();
        try { list.AddRange(DiscoverAsus()); }
        catch (Exception ex) { Log.Error("ASUS fans", ex); }
        try { if (sensors?.IsOpen == true) list.AddRange(DiscoverLhm(sensors)); }
        catch (Exception ex) { Log.Error("LHM fans", ex); }

        lock (_gate)
        {
            // Keep manual settings for fans that are still there.
            foreach (var ch in list)
                ch.Manual = _channels.FirstOrDefault(o => o.Id == ch.Id)?.Manual;
            _channels = list;
        }
        Log.Write($"Fans: {string.Join(", ", list.Select(c => $"{c.Group}/{c.Name}{(c.CanControl ? "" : " (read-only)")}"))}");
        ChannelsChanged?.Invoke();
    }

    private IEnumerable<FanChannel> DiscoverAsus()
    {
        if (!AsusAcpi.Available) yield break;
        IsAsus = true;
        AsusModel = Wmi.First("SELECT Model FROM Win32_ComputerSystem")?.S("Model") is { Length: > 0 } m ? m.Split('_')[0] : "ASUS";
        var fans = new (string Name, uint Fan, uint Curve, string Cools)[]
        {
            ("CPU fan", AsusAcpi.CpuFan, AsusAcpi.CpuCurve, "Cpu"),
            ("GPU fan", AsusAcpi.GpuFan, AsusAcpi.GpuCurve, "Gpu"),
            ("Middle fan", AsusAcpi.MidFan, AsusAcpi.MidCurve, ""),
        };
        foreach (var f in fans)
        {
            if (AsusAcpi.Get(f.Fan) == null) continue;
            var factory = new Dictionary<int, FanCurve>();
            foreach (int mode in new[] { AsusAcpi.ModePerformance, AsusAcpi.ModeTurbo, AsusAcpi.ModeSilent })
                if (AsusAcpi.FactoryCurve(f.Curve, mode) is { } c) factory[mode] = c;
            yield return new FanChannel
            {
                Id = "asus:" + f.Name,
                Name = f.Name,
                Group = AsusModel,
                Cools = f.Cools,
                CanControl = factory.Count > 0,
                Note = factory.Count > 0 ? null : "This fan's speed can be read but the firmware doesn't accept custom speeds.",
                AsusFan = f.Fan,
                AsusCurve = f.Curve,
                AsusFactory = factory,
            };
        }
    }

    private static IEnumerable<FanChannel> DiscoverLhm(HardwareSensors sensors)
    {
        foreach (var c in sensors.FanControls())
        {
            bool gpu = c.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
            yield return new FanChannel
            {
                Id = "lhm:" + c.Id,
                Name = gpu && c.Name.Equals("GPU Fan", StringComparison.OrdinalIgnoreCase) ? "GPU fan" : c.Name,
                Group = c.Hardware,
                Cools = gpu ? "Gpu" : c.HardwareType == "Cpu" ? "Cpu" : "",
                CanControl = true,
                LhmControlId = c.Id,
                LhmFanId = c.FanSensorId,
            };
        }
    }

    /// <summary>Reads every fan. Called from the sampler thread.</summary>
    public List<FanReading> Sample(List<HwNode>? hw, double cpuTemp, double gpuTemp)
    {
        var result = new List<FanReading>();
        List<FanChannel> channels;
        lock (_gate) channels = _channels;
        int? mode = IsAsus ? AsusMode : null;
        // LibreHardwareMonitor can report the same identifier twice, so no ToDictionary here.
        var lhmSensors = new Dictionary<string, double>();
        if (hw != null)
            foreach (var sensor in hw.SelectMany(h => h.AllSensors)) lhmSensors[sensor.Id] = sensor.Value;

        foreach (var ch in channels)
        {
            var r = new FanReading { Id = ch.Id };
            if (ch.AsusFan != 0)
            {
                r.Rpm = AsusAcpi.LatestFanRpm(ch.AsusFan);
                var auto = ch.AsusFactory != null && ch.AsusFactory.TryGetValue(mode ?? AsusAcpi.ModePerformance, out var c) ? c : null;
                // Laptop fans share heat pipes and the EC may follow either chip, so the hotter one is used:
                // that gives the higher (safer) estimate of what the automatic curve would run.
                double hottest = double.IsNaN(gpuTemp) ? cpuTemp : double.IsNaN(cpuTemp) ? gpuTemp : Math.Max(cpuTemp, gpuTemp);
                r.AutoPercent = auto?.DutyAt(hottest) ?? double.NaN;
                if (ch.Manual is int m) r.Percent = m;
            }
            else
            {
                if (ch.LhmFanId != null && lhmSensors.TryGetValue(ch.LhmFanId, out var rpm)) r.Rpm = rpm;
                if (ch.LhmControlId != null && lhmSensors.TryGetValue(ch.LhmControlId, out var duty)) r.Percent = duty;
            }
            result.Add(r);
        }
        return result;
    }

    /// <summary>Runs a fixed speed (0–100 %) on one fan.</summary>
    public bool SetManual(string id, int percent)
    {
        var ch = Find(id);
        if (ch is not { CanControl: true }) return false;
        percent = Math.Clamp(percent, 0, 100);
        bool ok;
        if (ch.AsusFan != 0)
        {
            ok = AsusAcpi.SetCurve(ch.AsusCurve, FanCurve.Fixed(percent));
            if (ok) _asusTouched = true;
        }
        else
        {
            ok = MonitorService.Instance?.Sensors.SetFanControl(ch.LhmControlId!, percent) == true;
        }
        if (ok) ch.Manual = percent;
        Log.Write($"Fan {ch.Group}/{ch.Name} → {percent}% {(ok ? "" : "(failed)")}");
        return ok;
    }

    /// <summary>Hands one fan back to automatic control.</summary>
    public bool SetAuto(string id)
    {
        var ch = Find(id);
        if (ch is not { CanControl: true }) return false;
        bool ok;
        if (ch.AsusFan != 0)
        {
            // Only re-applying the performance mode gives a fan back to the firmware, and it resets all fans,
            // so the other fans that are still on a fixed speed get theirs again right after.
            int mode = AsusMode ?? AsusAcpi.ModePerformance;
            ok = AsusAcpi.SetMode(mode);
            if (ok)
            {
                ch.Manual = null;
                foreach (var other in _channels.Where(c => c.AsusFan != 0 && c.Manual is int))
                    AsusAcpi.SetCurve(other.AsusCurve, FanCurve.Fixed(other.Manual!.Value));
            }
        }
        else
        {
            ok = MonitorService.Instance?.Sensors.SetFanControl(ch.LhmControlId!, null) == true;
        }
        if (ok) ch.Manual = null;
        Log.Write($"Fan {ch.Group}/{ch.Name} → automatic {(ok ? "" : "(failed)")}");
        return ok;
    }

    /// <summary>Switches the ASUS performance mode; the firmware resets all fans to that mode's automatic curves.</summary>
    public bool SetAsusMode(int mode)
    {
        if (!IsAsus || !AsusAcpi.SetMode(mode)) return false;
        _asusMode = mode;
        _asusTouched = true;
        Log.Write($"ASUS performance mode → {AsusAcpi.ModeName(mode)}");
        // Fans the user set manually keep their speed in the new mode.
        foreach (var ch in _channels.Where(c => c.AsusFan != 0 && c.Manual is int))
            SetManual(ch.Id, ch.Manual!.Value);
        return true;
    }

    /// <summary>Restores automatic control for every fan CPUx changed. Called on exit.</summary>
    public void RestoreAll()
    {
        foreach (var ch in _channels.Where(c => c.Manual != null && c.AsusFan == 0).ToList())
        {
            try { SetAuto(ch.Id); } catch (Exception ex) { Log.Error("Fan restore", ex); }
        }
        // One mode re-apply returns every ASUS fan to the firmware.
        if (_asusTouched)
        {
            foreach (var ch in _channels.Where(c => c.AsusFan != 0)) ch.Manual = null;
            try { AsusAcpi.SetMode(AsusMode ?? AsusAcpi.ModePerformance); } catch (Exception ex) { Log.Error("Fan restore", ex); }
            _asusTouched = false;
            Log.Write("ASUS fans returned to automatic");
        }
    }

    private FanChannel? Find(string id)
    {
        lock (_gate) return _channels.FirstOrDefault(c => c.Id == id);
    }
}
