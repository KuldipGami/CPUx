using CPUx.Core;

namespace CPUx.Services;

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string LuidKey { get; set; } = "";
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public ulong DedicatedBytes { get; set; }
    public ulong SharedBytes { get; set; }
    public string DriverVersion { get; set; } = "";
    public bool IsIntegrated { get; set; }
    public Nvml.Device? NvmlDevice { get; set; }
    public List<InfoSection> Sections { get; } = new();

    public string ShortName => Name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();
}

public static class GpuInfoReader
{
    public static string VendorName(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 => "Qualcomm",
        0x13B5 => "ARM",
        0x1AE0 => "Google",
        0x15AD => "VMware",
        0x80EE => "VirtualBox",
        _ => $"0x{id:X4}",
    };

    public static List<GpuInfo> Read()
    {
        var gpus = new List<GpuInfo>();
        var adapters = Native.EnumerateAdapters()
            .Where(a => !a.Software && a.VendorId != 0x1414)
            .GroupBy(a => a.LuidKey).Select(g => g.First())
            .ToList();
        var controllers = Wmi.Query("SELECT * FROM Win32_VideoController");
        var nvDevices = Nvml.Devices();
        var usedNv = new HashSet<IntPtr>();

        foreach (var a in adapters)
        {
            var g = new GpuInfo
            {
                Name = a.Description,
                VendorId = a.VendorId,
                DeviceId = a.DeviceId,
                Vendor = VendorName(a.VendorId),
                LuidKey = a.LuidKey,
                DedicatedBytes = a.DedicatedVideoMemory,
                SharedBytes = a.SharedSystemMemory,
                IsIntegrated = a.DedicatedVideoMemory < 600UL * 1024 * 1024 && a.VendorId is 0x8086 or 0x1002,
            };

            string ven = $"VEN_{a.VendorId:X4}", dev = $"DEV_{a.DeviceId:X4}";
            var wmi = controllers.FirstOrDefault(c =>
                          c.S("PNPDeviceID").Contains(ven, StringComparison.OrdinalIgnoreCase) &&
                          c.S("PNPDeviceID").Contains(dev, StringComparison.OrdinalIgnoreCase))
                      ?? controllers.FirstOrDefault(c => c.S("Name").Equals(a.Description, StringComparison.OrdinalIgnoreCase));

            if (a.VendorId == 0x10DE)
            {
                uint pciId = (a.DeviceId << 16) | a.VendorId;
                var nv = nvDevices.FirstOrDefault(d => !usedNv.Contains(d.Handle) && d.PciDeviceId == pciId)
                         ?? nvDevices.FirstOrDefault(d => !usedNv.Contains(d.Handle) && d.Name == a.Description);
                if (nv != null)
                {
                    usedNv.Add(nv.Handle);
                    g.NvmlDevice = nv;
                }
            }

            var adapter = new InfoSection("Adapter");
            adapter.Add("Name", g.Name)
                .Add("Vendor", g.Vendor)
                .Add("Type", g.IsIntegrated ? "Integrated" : "Discrete")
                .Add("Device ID", $"{a.VendorId:X4}:{a.DeviceId:X4}")
                .AddIf("Subsystem ID", a.SubSysId != 0 ? $"{a.SubSysId:X8}" : null)
                .AddIf("Revision", a.Revision.ToString())
                .Add("Dedicated video memory", Fmt.Bytes(a.DedicatedVideoMemory))
                .AddIf("Dedicated system memory", a.DedicatedSystemMemory > 0 ? Fmt.Bytes(a.DedicatedSystemMemory) : null)
                .Add("Shared system memory", Fmt.Bytes(a.SharedSystemMemory));
            if (wmi != null)
            {
                g.DriverVersion = wmi.S("DriverVersion");
                adapter.AddIf("Video processor", wmi.S("VideoProcessor"))
                    .AddIf("DAC type", wmi.S("AdapterDACType"))
                    .AddIf("Status", wmi.S("Status"));
            }
            g.Sections.Add(adapter);

            if (wmi != null)
            {
                var driver = new InfoSection("Driver");
                driver.Add("Version", wmi.S("DriverVersion"))
                    .Add("Date", Fmt.Date(wmi.D("DriverDate")))
                    .AddIf("INF file", wmi.S("InfFilename"))
                    .AddIf("INF section", wmi.S("InfSection"))
                    .AddIf("Installed display drivers", ShortenDrivers(wmi.S("InstalledDisplayDrivers")))
                    .AddIf("PNP device ID", wmi.S("PNPDeviceID"));
                g.Sections.Add(driver);

                if (wmi.L("CurrentHorizontalResolution") > 0)
                {
                    var mode = new InfoSection("Current display mode");
                    mode.Add("Resolution", $"{wmi.L("CurrentHorizontalResolution")} × {wmi.L("CurrentVerticalResolution")}")
                        .Add("Refresh rate", $"{wmi.L("CurrentRefreshRate")} Hz")
                        .AddIf("Color depth", wmi.L("CurrentBitsPerPixel") > 0 ? $"{wmi.L("CurrentBitsPerPixel")}-bit" : null)
                        .AddIf("Min / max refresh", wmi.L("MaxRefreshRate") > 0 ? $"{wmi.L("MinRefreshRate")} – {wmi.L("MaxRefreshRate")} Hz" : null);
                    g.Sections.Add(mode);
                }
            }

            if (g.NvmlDevice != null)
            {
                try { g.Sections.Add(Nvml.Details(g.NvmlDevice)); }
                catch (Exception ex) { Log.Error("NVML details", ex); }
            }
            gpus.Add(g);
        }

        // Fallback when DXGI enumeration failed entirely.
        if (gpus.Count == 0)
        {
            foreach (var c in controllers)
            {
                var g = new GpuInfo { Name = c.S("Name"), Vendor = c.S("AdapterCompatibility"), DriverVersion = c.S("DriverVersion") };
                var s = new InfoSection("Adapter");
                s.Add("Name", g.Name).Add("Vendor", g.Vendor).Add("Driver version", g.DriverVersion)
                 .Add("Driver date", Fmt.Date(c.D("DriverDate"))).AddIf("PNP device ID", c.S("PNPDeviceID"));
                g.Sections.Add(s);
                gpus.Add(g);
            }
        }

        // Discrete GPUs first.
        return gpus.OrderBy(g => g.IsIntegrated).ToList();
    }

    private static string ShortenDrivers(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var names = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.IO.Path.GetFileName).Distinct().Take(4);
        return string.Join(", ", names);
    }
}
