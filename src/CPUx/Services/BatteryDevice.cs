using System.Runtime.InteropServices;
using System.Text;
using CPUx.Core;
using Microsoft.Win32.SafeHandles;

namespace CPUx.Services;

/// <summary>Reads battery data straight from the battery class driver (no admin needed).</summary>
public sealed class BatteryDevice : IDisposable
{
    private static Guid BatteryClass = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved1, Reserved2, Reserved3;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BATTERY_STATUS
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfoData);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref BATTERY_WAIT_STATUS inBuf, int inSize, out BATTERY_STATUS outBuf, int outSize, out int returned, IntPtr overlapped);

    private readonly SafeFileHandle _handle;
    private readonly uint _tag;

    private BatteryDevice(SafeFileHandle handle, uint tag)
    {
        _handle = handle;
        _tag = tag;
    }

    public static List<BatteryDevice> Open()
    {
        var list = new List<BatteryDevice>();
        IntPtr devs = SetupDiGetClassDevs(ref BatteryClass, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x10);
        if (devs == new IntPtr(-1)) return list;
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref BatteryClass, i, ref did)) break;
                SetupDiGetDeviceInterfaceDetail(devs, ref did, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devs, ref did, detail, required, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    var h = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    uint wait = 0;
                    if (DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out uint tag, 4, out _, IntPtr.Zero) && tag != 0)
                        list.Add(new BatteryDevice(h, tag));
                    else
                        h.Dispose();
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Battery open", ex);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devs);
        }
        return list;
    }

    private bool Query(int level, IntPtr buffer, int size, out int returned)
    {
        var q = new BATTERY_QUERY_INFORMATION { BatteryTag = _tag, InformationLevel = level };
        return DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(), buffer, size, out returned, IntPtr.Zero);
    }

    public BATTERY_INFORMATION? Information()
    {
        int size = Marshal.SizeOf<BATTERY_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            return Query(0, buf, size, out _) ? Marshal.PtrToStructure<BATTERY_INFORMATION>(buf) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public string Text(int level)
    {
        IntPtr buf = Marshal.AllocHGlobal(512);
        try
        {
            if (!Query(level, buf, 512, out int ret) || ret <= 0) return "";
            return (Marshal.PtrToStringUni(buf, ret / 2) ?? "").TrimEnd('\0').Trim();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public DateTime? ManufactureDate()
    {
        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            if (!Query(5, buf, 4, out _)) return null;
            int day = Marshal.ReadByte(buf, 0), month = Marshal.ReadByte(buf, 1), year = (ushort)Marshal.ReadInt16(buf, 2);
            if (year < 1990 || month is < 1 or > 12 || day is < 1 or > 31) return null;
            return new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public double TemperatureC()
    {
        IntPtr buf = Marshal.AllocHGlobal(4);
        try
        {
            if (!Query(2, buf, 4, out _)) return double.NaN;
            uint tenthsKelvin = (uint)Marshal.ReadInt32(buf);
            return tenthsKelvin == 0 ? double.NaN : tenthsKelvin / 10.0 - 273.15;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public BATTERY_STATUS? Status()
    {
        var w = new BATTERY_WAIT_STATUS { BatteryTag = _tag };
        return DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_STATUS, ref w, Marshal.SizeOf<BATTERY_WAIT_STATUS>(), out var s, Marshal.SizeOf<BATTERY_STATUS>(), out _, IntPtr.Zero)
            ? s : null;
    }

    public static string Chemistry(uint fourcc)
    {
        var s = Encoding.ASCII.GetString(BitConverter.GetBytes(fourcc)).Trim('\0', ' ').ToUpperInvariant();
        return s switch
        {
            "LION" or "LI-I" => "Lithium-ion",
            "LIP" or "LIPO" or "LI-P" => "Lithium polymer",
            "PBAC" => "Lead acid",
            "NIMH" => "Nickel metal hydride",
            "NICD" => "Nickel cadmium",
            "RAM" => "Rechargeable alkaline-manganese",
            "" => Fmt.Dash,
            _ => $"Unknown ({s})",
        };
    }

    public void Dispose() => _handle.Dispose();
}
