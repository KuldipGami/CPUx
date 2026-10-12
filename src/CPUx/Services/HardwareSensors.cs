using CPUx.Core;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace CPUx.Services;

public sealed record SensorReading(string Id, string Name, string Type, double Value, double Min, double Max);

public sealed class HwNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public List<SensorReading> Sensors { get; } = new();
    public List<HwNode> Children { get; } = new();

    public IEnumerable<SensorReading> AllSensors => Sensors.Concat(Children.SelectMany(c => c.AllSensors));

    public double Find(string type, Func<string, bool> name)
    {
        foreach (var s in AllSensors)
            if (s.Type == type && name(s.Name) && !double.IsNaN(s.Value)) return s.Value;
        return double.NaN;
    }

    public double Find(string type, params string[] exactNames)
    {
        foreach (var n in exactNames)
        {
            var v = Find(type, s => s.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (!double.IsNaN(v)) return v;
        }
        return double.NaN;
    }
}

/// <summary>Wraps LibreHardwareMonitor: temperatures, clocks, voltages, fans, power.</summary>
public sealed class HardwareSensors : IDisposable
{
    private Computer? _computer;
    private readonly object _gate = new();

    public bool IsOpen { get; private set; }
    public string? Error { get; private set; }

    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch { return false; }
    }

    public void Open()
    {
        lock (_gate)
        {
            if (IsOpen) return;
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,
                    IsStorageEnabled = true,
                    IsBatteryEnabled = true,
                    IsPsuEnabled = true,
                    IsNetworkEnabled = false,
                };
                _computer.Open();
                IsOpen = true;
                Log.Write($"LibreHardwareMonitor opened: {_computer.Hardware.Count} hardware nodes");
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Log.Error("LHM open", ex);
            }
        }
    }

    public List<HwNode> Sample()
    {
        var result = new List<HwNode>();
        lock (_gate)
        {
            if (!IsOpen || _computer == null) return result;
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) sub.Update();
                    result.Add(Convert(hw));
                }
                catch (Exception ex)
                {
                    Log.Error($"LHM update {hw.Name}", ex);
                }
            }
        }
        return result;
    }

    private static HwNode Convert(IHardware hw)
    {
        var node = new HwNode { Id = hw.Identifier.ToString(), Name = hw.Name, Type = hw.HardwareType.ToString() };
        foreach (var s in hw.Sensors)
        {
            node.Sensors.Add(new SensorReading(
                s.Identifier.ToString(), s.Name, s.SensorType.ToString(),
                s.Value ?? double.NaN, s.Min ?? double.NaN, s.Max ?? double.NaN));
        }
        foreach (var sub in hw.SubHardware) node.Children.Add(Convert(sub));
        return node;
    }

    /// <summary>A fan output LibreHardwareMonitor can drive (motherboard Super I/O, NVIDIA/AMD GPU coolers).</summary>
    public sealed record FanControlInfo(string Id, string Hardware, string HardwareType, string Name, string? FanSensorId, double Min, double Max);

    public List<FanControlInfo> FanControls()
    {
        var list = new List<FanControlInfo>();
        lock (_gate)
        {
            if (!IsOpen || _computer == null) return list;
            foreach (var hw in _computer.Hardware.SelectMany(h => h.SubHardware.Prepend(h)))
            {
                foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
                {
                    var fan = hw.Sensors.FirstOrDefault(f => f.SensorType == SensorType.Fan && f.Index == s.Index);
                    list.Add(new FanControlInfo(s.Identifier.ToString(), hw.Name, hw.HardwareType.ToString(), s.Name,
                        fan?.Identifier.ToString(), s.Control.MinSoftwareValue, s.Control.MaxSoftwareValue));
                }
            }
        }
        return list;
    }

    /// <summary>Sets a fan output to a fixed percentage, or back to the hardware's automatic control when null.</summary>
    public bool SetFanControl(string id, double? percent)
    {
        lock (_gate)
        {
            if (!IsOpen || _computer == null) return false;
            var sensor = _computer.Hardware.SelectMany(h => h.SubHardware.Prepend(h)).SelectMany(h => h.Sensors)
                .FirstOrDefault(s => s.Identifier.ToString() == id);
            if (sensor?.Control is not { } control) return false;
            if (percent is double p) control.SetSoftware((float)Math.Clamp(p, control.MinSoftwareValue, control.MaxSoftwareValue));
            else control.SetDefault();
            return true;
        }
    }

    public void ResetMinMax()
    {
        lock (_gate)
        {
            if (_computer == null) return;
            foreach (var hw in _computer.Hardware) Reset(hw);
        }
    }

    private static void Reset(IHardware hw)
    {
        foreach (var s in hw.Sensors) { s.ResetMin(); s.ResetMax(); }
        foreach (var sub in hw.SubHardware) Reset(sub);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            IsOpen = false;
        }
    }
}
