using System.Runtime.InteropServices;
using CPUx.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CPUx.Services;

/// <summary>
/// ASUS laptop firmware interface (ATKACPI device, provided by the "ASUS System Control Interface" driver).
/// Gives fan speeds, fan curves, the CPU temperature and the performance mode (Silent / Performance / Turbo)
/// without administrator rights. Same method IDs as Armoury Crate and G-Helper use.
/// </summary>
public static class AsusAcpi
{
    private const uint IoctlControl = 0x0022240C;
    private const uint Dsts = 0x53545344; // "DSTS" — get device status
    private const uint Devs = 0x53564544; // "DEVS" — set device status

    public const uint CpuFan = 0x00110013, GpuFan = 0x00110014, MidFan = 0x00110031;
    public const uint CpuCurve = 0x00110024, GpuCurve = 0x00110025, MidCurve = 0x00110032;
    public const uint CpuTempId = 0x00120094;
    public const uint PerformanceModeId = 0x00120075;

    // Firmware performance modes. Armoury Crate calls 0 "Performance"; the fan-curve query numbers them differently.
    public const int ModePerformance = 0, ModeTurbo = 1, ModeSilent = 2;

    private static readonly object Gate = new();
    private static SafeFileHandle? _handle;
    private static bool _probed;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                if (_probed) return _handle != null;
                _probed = true;
                try
                {
                    var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h.IsInvalid) { h.Dispose(); return false; }
                    _handle = h;
                    Log.Write("ASUS ATKACPI interface available");
                    return true;
                }
                catch { return false; }
            }
        }
    }

    private static byte[]? Call(uint method, byte[] args)
    {
        if (!Available) return null;
        var input = new byte[8 + args.Length];
        BitConverter.GetBytes(method).CopyTo(input, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(input, 4);
        args.CopyTo(input, 8);
        var output = new byte[16];
        lock (Gate)
        {
            return DeviceIoControl(_handle!, IoctlControl, input, input.Length, output, output.Length, out _, IntPtr.Zero) ? output : null;
        }
    }

    private static byte[] Args(uint device, uint value)
    {
        var a = new byte[8];
        BitConverter.GetBytes(device).CopyTo(a, 0);
        BitConverter.GetBytes(value).CopyTo(a, 4);
        return a;
    }

    /// <summary>Reads a device value; null if the device is missing (presence bit 0x10000 not set).</summary>
    public static int? Get(uint device)
    {
        var r = Call(Dsts, Args(device, 0));
        if (r == null) return null;
        uint v = BitConverter.ToUInt32(r, 0);
        if ((v & 0x10000) == 0 || v == 0xFFFFFFFE) return null;
        return (int)(v & 0xFFFF);
    }

    public static bool Set(uint device, uint value)
    {
        var r = Call(Devs, Args(device, value));
        return r != null && BitConverter.ToUInt32(r, 0) == 1;
    }

    // ------------------------------------------------------------------ background readings
    // The embedded controller answers in ~10 ms but can stall for much longer while other software (Armoury Crate)
    // talks to it, so live values are read on their own thread and the sampler only picks up the latest ones.
    private static readonly uint[] PolledFans = { CpuFan, GpuFan, MidFan };
    private static readonly double[] _rpm = { double.NaN, double.NaN, double.NaN };
    private static double _cpuTemp = double.NaN;
    private static Thread? _poller;

    private static void EnsurePolling()
    {
        if (_poller != null || !Available) return;
        lock (PolledFans)
        {
            if (_poller != null) return;
            _poller = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        Volatile.Write(ref _cpuTemp, CpuTemperature());
                        for (int i = 0; i < PolledFans.Length; i++) Volatile.Write(ref _rpm[i], FanRpm(PolledFans[i]));
                    }
                    catch (Exception ex) { Log.Error("ASUS poll", ex); }
                    Thread.Sleep(Math.Clamp(AppSettings.Current.IntervalMs, 500, 2000));
                }
            }) { IsBackground = true, Name = "CPUx ASUS EC" };
            _poller.Start();
        }
    }

    /// <summary>Latest CPU temperature from the background reader (NaN until the first read).</summary>
    public static double LatestCpuTemperature
    {
        get { EnsurePolling(); return Volatile.Read(ref _cpuTemp); }
    }

    /// <summary>Latest fan speed from the background reader.</summary>
    public static double LatestFanRpm(uint fanDevice)
    {
        EnsurePolling();
        int i = Array.IndexOf(PolledFans, fanDevice);
        return i < 0 ? double.NaN : Volatile.Read(ref _rpm[i]);
    }

    /// <summary>Fan speed in RPM (the firmware reports hundreds of RPM).</summary>
    public static double FanRpm(uint fanDevice)
    {
        var v = Get(fanDevice);
        return v is > 0 and <= 250 ? v.Value * 100 : v == 0 ? 0 : double.NaN;
    }

    public static double CpuTemperature()
    {
        var v = Get(CpuTempId);
        return v is > 10 and < 120 ? v.Value : double.NaN;
    }

    /// <summary>
    /// Factory fan curve for a performance mode: 8 temperatures (°C) and 8 duty values (%).
    /// Returns null when the fan has no curve support or the data doesn't look like a curve.
    /// </summary>
    public static FanCurve? FactoryCurve(uint curveDevice, int mode)
    {
        uint query = mode switch { ModeSilent => 1u, ModeTurbo => 2u, _ => 0u };
        var r = Call(Dsts, Args(curveDevice, query));
        if (r == null) return null;
        var c = new FanCurve(r[..8], r[8..16]);
        return c.IsValid ? c : null;
    }

    public static bool SetCurve(uint curveDevice, FanCurve curve)
    {
        var args = new byte[20];
        BitConverter.GetBytes(curveDevice).CopyTo(args, 0);
        curve.Temps.CopyTo(args, 4);
        // Some ASUS BIOSes misbehave when a point is exactly 100%, so the top is 99% like Armoury Crate.
        for (int i = 0; i < 8; i++) args[12 + i] = Math.Min((byte)99, curve.Duty[i]);
        var r = Call(Devs, args);
        return r != null && BitConverter.ToUInt32(r, 0) == 1;
    }

    /// <summary>
    /// Re-applies a performance mode; the firmware then returns every fan to its own automatic control.
    /// (Writing the factory curve back is not the same: custom curves only follow the fan's own chip.)
    /// </summary>
    public static bool SetMode(int mode) => Set(PerformanceModeId, (uint)mode);

    /// <summary>Current mode as Armoury Crate last set it (the firmware can't report it), or null if unknown.</summary>
    public static int? ArmouryCrateMode(bool onAc)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASUS\Armoury Crate Service\ThrottlePlugin\ROG ATKStatus");
            if (key?.GetValue(onAc ? "ThrottleModeOnAC" : "ThrottleModeOnDC") is int m && m is ModePerformance or ModeTurbo or ModeSilent) return m;
        }
        catch { }
        return null;
    }

    public static string ModeName(int mode) => mode switch
    {
        ModeSilent => "Silent",
        ModeTurbo => "Turbo",
        _ => "Performance",
    };
}

public sealed class FanCurve
{
    public FanCurve(byte[] temps, byte[] duty)
    {
        Temps = temps;
        Duty = duty;
    }

    public byte[] Temps { get; }
    public byte[] Duty { get; }

    public bool IsValid
    {
        get
        {
            if (Temps.Length != 8 || Duty.Length != 8) return false;
            for (int i = 0; i < 8; i++)
            {
                if (Temps[i] is < 10 or > 120 || Duty[i] > 100) return false;
                if (i > 0 && Temps[i] < Temps[i - 1]) return false;
            }
            return Duty.Any(d => d > 0);
        }
    }

    /// <summary>
    /// A fixed duty at every temperature. A custom curve is evaluated against the fan's own chip temperature,
    /// and below the first point the EC slows the fan down, so the points start low (20 °C) to cover every
    /// temperature the fan can see — otherwise a cool GPU would let "60 %" slow the GPU fan to a stop.
    /// </summary>
    public static FanCurve Fixed(int percent)
    {
        byte d = (byte)Math.Clamp(percent, 0, 100);
        return new FanCurve(new byte[] { 20, 30, 40, 50, 60, 70, 80, 90 }, Enumerable.Repeat(d, 8).ToArray());
    }

    /// <summary>Duty the curve asks for at a temperature (step function, like the EC).</summary>
    public double DutyAt(double temp)
    {
        if (double.IsNaN(temp)) return double.NaN;
        double duty = Duty[0];
        for (int i = 0; i < 8; i++)
            if (temp >= Temps[i]) duty = Duty[i];
        return duty;
    }
}
