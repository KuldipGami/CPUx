using System.Runtime.InteropServices;
using System.Text;
using CPUx.Core;
using Microsoft.Win32;

namespace CPUx.Services;

public sealed class DisplayInfo
{
    public string Name { get; set; } = "Display";
    public string GdiName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double RefreshHz { get; set; }
    public int ScalePercent { get; set; } = 100;
    public bool Primary { get; set; }
    public bool Internal { get; set; }
    public double DiagonalInches { get; set; }
    public string Connection { get; set; } = "";
    public List<InfoSection> Sections { get; } = new();

    public string Summary => $"{Width} × {Height} · {RefreshHz:0.##} Hz" + (DiagonalInches > 0 ? $" · {DiagonalInches:0.#}\"" : "");
}

public static class DisplayReader
{
    private static readonly Dictionary<string, string> PnpVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer", ["AOC"] = "AOC", ["APP"] = "Apple", ["AUO"] = "AU Optronics", ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ", ["BOE"] = "BOE", ["CMN"] = "Innolux", ["CSO"] = "CSOT", ["DEL"] = "Dell",
        ["ENC"] = "EIZO", ["GSM"] = "LG Electronics", ["HPN"] = "HP", ["HWP"] = "HP", ["IVO"] = "InfoVision",
        ["LEN"] = "Lenovo", ["LGD"] = "LG Display", ["MSI"] = "MSI", ["NEC"] = "NEC", ["PHL"] = "Philips",
        ["SAM"] = "Samsung", ["SDC"] = "Samsung Display", ["SEC"] = "Samsung", ["SHP"] = "Sharp", ["SNY"] = "Sony",
        ["TMX"] = "Tianma", ["VSC"] = "ViewSonic", ["GBT"] = "Gigabyte", ["XMI"] = "Xiaomi", ["HKC"] = "HKC",
        ["NCP"] = "Najing CEC Panda", ["MEI"] = "Panasonic", ["IOC"] = "Iiyama", ["KTC"] = "KTC",
    };

    private static string OutputName(int tech) => tech switch
    {
        -1 => "Other",
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (internal)",
        8 => "D-Jpn",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "Embedded DisplayPort (internal)",
        12 => "UDI",
        13 => "Embedded UDI (internal)",
        14 => "SDTV dongle",
        15 => "Miracast (wireless)",
        16 => "Indirect (wired)",
        17 => "Indirect (virtual)",
        unchecked((int)0x80000000) => "Internal",
        _ => $"Type {tech}",
    };

    public static List<DisplayInfo> Read(IReadOnlyList<GpuInfo> gpus)
    {
        var result = new List<DisplayInfo>();
        try
        {
            var scales = MonitorScales();
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[np];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[nm];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;

            for (int i = 0; i < np; i++)
            {
                var path = paths[i];
                var d = new DisplayInfo();

                var target = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = 2;
                target.header.size = Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                bool hasTarget = Native.DisplayConfigGetDeviceInfo(ref target) == 0;

                var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = 1;
                source.header.size = Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref source) == 0) d.GdiName = source.viewGdiDeviceName;

                var color = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                color.header.type = 9;
                color.header.size = Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                color.header.adapterId = path.targetInfo.adapterId;
                color.header.id = path.targetInfo.id;
                bool hasColor = Native.DisplayConfigGetDeviceInfo(ref color) == 0;

                var rr = path.targetInfo.refreshRate;
                d.RefreshHz = rr.Denominator == 0 ? 0 : (double)rr.Numerator / rr.Denominator;
                int tech = hasTarget ? target.outputTechnology : path.targetInfo.outputTechnology;
                d.Connection = OutputName(tech);
                d.Internal = tech is 6 or 11 or 13 || tech == unchecked((int)0x80000000);

                // Desktop position/size from the GDI device.
                var dm = new Native.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
                uint bpp = 0;
                if (!string.IsNullOrEmpty(d.GdiName) && Native.EnumDisplaySettings(d.GdiName, Native.ENUM_CURRENT_SETTINGS, ref dm))
                {
                    d.Width = (int)dm.dmPelsWidth;
                    d.Height = (int)dm.dmPelsHeight;
                    d.X = dm.dmPositionX;
                    d.Y = dm.dmPositionY;
                    bpp = dm.dmBitsPerPel;
                    d.Primary = d.X == 0 && d.Y == 0;
                }
                if (scales.TryGetValue(d.GdiName, out int scale)) d.ScalePercent = scale;

                string pnp = "";
                if (hasTarget)
                {
                    ushort mid = (ushort)((target.edidManufactureId >> 8) | (target.edidManufactureId << 8));
                    pnp = new string(new[] { (char)('A' - 1 + ((mid >> 10) & 31)), (char)('A' - 1 + ((mid >> 5) & 31)), (char)('A' - 1 + (mid & 31)) });
                }

                var edid = ReadEdid(hasTarget ? target.monitorDevicePath : "");
                string friendly = hasTarget ? target.monitorFriendlyDeviceName : "";
                if (string.IsNullOrWhiteSpace(friendly)) friendly = edid?.Name ?? "";
                if (string.IsNullOrWhiteSpace(friendly)) friendly = d.Internal ? "Built-in display" : "Generic monitor";
                d.Name = friendly;

                double wCm = edid?.WidthCm ?? 0, hCm = edid?.HeightCm ?? 0;
                if (wCm > 0 && hCm > 0) d.DiagonalInches = Math.Sqrt(wCm * wCm + hCm * hCm) / 2.54;

                string adapterKey = $"luid_0x{path.targetInfo.adapterId.HighPart:X8}_0x{path.targetInfo.adapterId.LowPart:X8}";
                string gpuName = gpus.FirstOrDefault(g => g.LuidKey.Equals(adapterKey, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

                var main = new InfoSection("Display");
                main.Add("Name", d.Name)
                    .Add("Manufacturer", PnpVendors.TryGetValue(pnp, out var vendor) ? $"{vendor} ({pnp})" : pnp)
                    .AddIf("Product code", hasTarget ? $"{target.edidProductCodeId:X4}" : null)
                    .Add("Connection", d.Connection)
                    .Add("Primary display", Fmt.YesNo(d.Primary))
                    .AddIf("Graphics adapter", gpuName)
                    .AddIf("GDI device", d.GdiName);
                d.Sections.Add(main);

                var mode = new InfoSection("Current mode");
                mode.Add("Resolution", $"{d.Width} × {d.Height}")
                    .Add("Aspect ratio", AspectRatio(d.Width, d.Height))
                    .Add("Refresh rate", $"{d.RefreshHz:0.###} Hz")
                    .Add("Scaling", $"{d.ScalePercent}%")
                    .Add("Effective resolution", d.ScalePercent > 0 ? $"{d.Width * 100 / d.ScalePercent} × {d.Height * 100 / d.ScalePercent}" : Fmt.Dash)
                    .AddIf("Desktop color depth", bpp > 0 ? $"{bpp}-bit" : null)
                    .Add("Position", $"{d.X}, {d.Y}");
                if (hasColor)
                {
                    bool supported = (color.value & 1) != 0, enabled = (color.value & 2) != 0;
                    mode.Add("HDR / advanced color", supported ? (enabled ? "Supported, on" : "Supported, off") : "Not supported")
                        .AddIf("Bits per color channel", color.bitsPerColorChannel > 0 ? color.bitsPerColorChannel.ToString() : null)
                        .Add("Color encoding", color.colorEncoding switch { 0 => "RGB", 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", 4 => "Intensity", _ => Fmt.Dash });
                }
                d.Sections.Add(mode);

                if (edid != null)
                {
                    var e = new InfoSection("EDID");
                    e.AddIf("Monitor name", edid.Name)
                     .AddIf("Serial number", edid.Serial)
                     .AddIf("Physical size", wCm > 0 ? $"{wCm:0.#} × {hCm:0.#} cm" : null)
                     .AddIf("Diagonal", d.DiagonalInches > 0 ? $"{d.DiagonalInches:0.#}\"" : null)
                     .AddIf("Pixel density", d.DiagonalInches > 0 && d.Width > 0 ? $"{Math.Sqrt((double)d.Width * d.Width + (double)d.Height * d.Height) / d.DiagonalInches:0} PPI" : null)
                     .AddIf("Native resolution", edid.NativeWidth > 0 ? $"{edid.NativeWidth} × {edid.NativeHeight}" : null)
                     .AddIf("Manufactured", edid.Year > 1990 ? (edid.Week is > 0 and < 54 ? $"Week {edid.Week}, {edid.Year}" : edid.Year.ToString()) : null)
                     .AddIf("EDID version", edid.Version)
                     .AddIf("Input", edid.Digital ? "Digital" : "Analog")
                     .AddIf("Gamma", edid.Gamma > 0 ? edid.Gamma.ToString("0.00") : null);
                    d.Sections.Add(e);
                }

                result.Add(d);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Displays", ex);
        }
        if (result.Count > 0 && !result.Any(r => r.Primary)) result[0].Primary = true;
        return result.OrderByDescending(r => r.Primary).ThenBy(r => r.X).ToList();
    }

    private static string AspectRatio(int w, int h)
    {
        if (w <= 0 || h <= 0) return Fmt.Dash;
        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
        int g = Gcd(w, h);
        int rw = w / g, rh = h / g;
        if (rw == 8 && rh == 5) return "16:10";
        if (rw > 50) return $"{(double)w / h:0.00}:1";
        return $"{rw}:{rh}";
    }

    private static Dictionary<string, int> MonitorScales()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                if (Native.GetMonitorInfo(hMon, ref mi) && Native.GetDpiForMonitor(hMon, 0, out uint dx, out _) == 0)
                    map[mi.szDevice] = (int)Math.Round(dx * 100 / 96.0);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("Monitor DPI", ex); }
        return map;
    }

    private sealed class Edid
    {
        public string Name = "", Serial = "", Version = "";
        public int Year, Week, NativeWidth, NativeHeight;
        public double WidthCm, HeightCm, Gamma;
        public bool Digital;
    }

    private static Edid? ReadEdid(string devicePath)
    {
        // \\?\DISPLAY#SAM0F9B#5&1a2b&0&UID4352#{guid}  ->  SYSTEM\CurrentControlSet\Enum\DISPLAY\SAM0F9B\5&1a2b&0&UID4352
        if (string.IsNullOrEmpty(devicePath)) return null;
        try
        {
            var parts = devicePath.Replace(@"\\?\", "").Split('#');
            if (parts.Length < 3) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] b || b.Length < 128) return null;
            var e = new Edid
            {
                Week = b[16],
                Year = b[17] + 1990,
                Version = $"{b[18]}.{b[19]}",
                Digital = (b[20] & 0x80) != 0,
                WidthCm = b[21],
                HeightCm = b[22],
                Gamma = b[23] == 0xFF ? 0 : (b[23] + 100) / 100.0,
            };
            for (int off = 54; off <= 108; off += 18)
            {
                bool timing = b[off] != 0 || b[off + 1] != 0;
                if (timing)
                {
                    if (e.NativeWidth == 0)
                    {
                        e.NativeWidth = b[off + 2] | ((b[off + 4] & 0xF0) << 4);
                        e.NativeHeight = b[off + 5] | ((b[off + 7] & 0xF0) << 4);
                        int wMm = b[off + 12] | ((b[off + 14] & 0xF0) << 4);
                        int hMm = b[off + 13] | ((b[off + 14] & 0x0F) << 8);
                        if (wMm > 0 && hMm > 0) { e.WidthCm = wMm / 10.0; e.HeightCm = hMm / 10.0; }
                    }
                    continue;
                }
                string text = Encoding.ASCII.GetString(b, off + 5, 13).Split('\n')[0].Trim();
                switch (b[off + 3])
                {
                    case 0xFC: e.Name = text; break;
                    case 0xFF: e.Serial = text; break;
                }
            }
            return e;
        }
        catch
        {
            return null;
        }
    }
}
