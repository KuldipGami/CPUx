using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CPUx.Core;
using Microsoft.Win32;

namespace CPUx.Services;

public sealed class MemoryInfo
{
    public ulong InstalledBytes { get; set; }
    public string Type { get; set; } = "";
    public int SpeedMTs { get; set; }
    public int SlotsUsed { get; set; }
    public int SlotsTotal { get; set; }
    public string FormFactor { get; set; } = "";
    public List<InfoSection> Modules { get; } = new();
    public List<InfoSection> Sections { get; } = new();

    public string Summary =>
        $"{Fmt.Bytes(InstalledBytes, 0)}" + (Type != "" ? $" {Type}" : "") + (SpeedMTs > 0 ? $" @ {SpeedMTs} MT/s" : "");
}

public sealed class DiskInfo
{
    public int Number { get; set; }
    public string Model { get; set; } = "";
    public string Media { get; set; } = "";
    public string Bus { get; set; } = "";
    public ulong SizeBytes { get; set; }
    public string Health { get; set; } = "";
    public bool IsSystem { get; set; }
    public List<string> Letters { get; } = new();
    public List<InfoSection> Sections { get; } = new();
}

public sealed class BatteryInfo
{
    public bool Present { get; set; }
    public string Name { get; set; } = "";
    public double DesignWh { get; set; } = double.NaN;
    public double FullWh { get; set; } = double.NaN;
    public int CycleCount { get; set; } = -1;
    public List<InfoSection> Sections { get; } = new();
    public double WearPercent => DesignWh > 0 && FullWh > 0 ? Math.Max(0, (1 - FullWh / DesignWh) * 100) : double.NaN;
}

public sealed class DeviceEntry
{
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Status { get; init; } = "";
    public int ErrorCode { get; init; }
    public string DeviceId { get; init; } = "";
    public string Driver { get; init; } = "";
    public bool HasProblem => ErrorCode != 0 && ErrorCode != 22;
    public bool Disabled => ErrorCode == 22;
}

public static class SystemInfoReader
{
    public static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ Memory
    private static readonly Dictionary<string, string> Jedec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung", ["CE00"] = "Samsung", ["00CE"] = "Samsung",
        ["802C"] = "Micron", ["2C00"] = "Micron", ["002C"] = "Micron",
        ["80AD"] = "SK hynix", ["AD00"] = "SK hynix", ["00AD"] = "SK hynix",
        ["0198"] = "Kingston", ["9801"] = "Kingston", ["029E"] = "Corsair", ["9E02"] = "Corsair",
        ["04CD"] = "G.Skill", ["CD04"] = "G.Skill", ["859B"] = "Crucial", ["059B"] = "Crucial", ["9B05"] = "Crucial",
        ["0B03"] = "Nanya", ["830B"] = "Nanya", ["8551"] = "Qimonda", ["04CB"] = "A-DATA", ["CB04"] = "A-DATA",
        ["8A45"] = "CXMT", ["0443"] = "Ramaxel", ["04F1"] = "Toshiba", ["017A"] = "Apacer", ["7A01"] = "Apacer",
        ["0315"] = "Team Group", ["1315"] = "Team Group", ["02FE"] = "Elpida", ["FE02"] = "Elpida",
    };

    private static string MemoryVendor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Fmt.Dash;
        string r = raw.Trim();
        string key = r.Length >= 4 ? r[..4] : r;
        return Jedec.TryGetValue(key, out var name) ? $"{name}" : r;
    }

    private static string MemoryType(long smbios, long legacy) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR",
        28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5",
        _ => legacy switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", _ => "" },
    };

    private static string FormFactor(long f) => f switch
    {
        8 => "DIMM", 12 => "SO-DIMM", 13 => "SRIMM", 9 => "TSOP", 11 => "RIMM", 0 => "", 1 => "Other", 2 => "Unknown", _ => $"Type {f}",
    };

    public static MemoryInfo Memory()
    {
        var info = new MemoryInfo();
        var modules = Wmi.Query("SELECT * FROM Win32_PhysicalMemory");
        int i = 0;
        foreach (var m in modules)
        {
            i++;
            ulong cap = m.U("Capacity");
            info.InstalledBytes += cap;
            string type = MemoryType(m.L("SMBIOSMemoryType"), m.L("MemoryType"));
            if (info.Type == "") info.Type = type;
            int speed = (int)m.L("ConfiguredClockSpeed");
            if (speed == 0) speed = (int)m.L("Speed");
            if (info.SpeedMTs == 0) info.SpeedMTs = speed;
            string ff = FormFactor(m.L("FormFactor"));
            if (info.FormFactor == "") info.FormFactor = ff;

            string slot = m.S("DeviceLocator");
            var s = new InfoSection(string.IsNullOrEmpty(slot) ? $"Module {i}" : slot);
            s.Add("Capacity", Fmt.Bytes(cap, 0))
             .Add("Type", string.IsNullOrEmpty(type) ? Fmt.Dash : $"{type} {ff}".Trim())
             .Add("Manufacturer", MemoryVendor(m.S("Manufacturer")))
             .AddIf("Part number", m.S("PartNumber"))
             .AddIf("Serial number", m.S("SerialNumber"))
             .AddIf("Rated speed", m.L("Speed") > 0 ? $"{m.L("Speed")} MT/s" : null)
             .AddIf("Configured speed", m.L("ConfiguredClockSpeed") > 0 ? $"{m.L("ConfiguredClockSpeed")} MT/s" : null)
             .AddIf("Voltage", m.L("ConfiguredVoltage") > 0 ? $"{m.L("ConfiguredVoltage") / 1000.0:0.00} V" : null)
             .AddIf("Voltage range", m.L("MinVoltage") > 0 ? $"{m.L("MinVoltage") / 1000.0:0.00} – {m.L("MaxVoltage") / 1000.0:0.00} V" : null)
             .AddIf("Data width", m.L("DataWidth") > 0 ? $"{m.L("DataWidth")}-bit" + (m.L("TotalWidth") > m.L("DataWidth") ? $" (total {m.L("TotalWidth")}-bit, ECC)" : "") : null)
             .AddIf("Rank", m.L("Attributes") > 0 ? m.L("Attributes") switch { 1 => "Single", 2 => "Dual", 4 => "Quad", var r => r.ToString() } : null)
             .AddIf("Bank", m.S("BankLabel"))
             .AddIf("Slot", slot);
            info.Modules.Add(s);
        }
        info.SlotsUsed = modules.Count;

        if (Native.GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0 && info.InstalledBytes == 0)
            info.InstalledBytes = kb * 1024;

        var arr = Wmi.Query("SELECT * FROM Win32_PhysicalMemoryArray");
        info.SlotsTotal = (int)arr.Sum(a => a.L("MemoryDevices"));
        var mst = Native.MemoryStatus();

        var summary = new InfoSection("Physical memory");
        summary.Add("Installed", Fmt.Bytes(info.InstalledBytes, 1))
            .Add("Usable by Windows", Fmt.Bytes(mst.ullTotalPhys, 1))
            .Add("Hardware reserved", Fmt.Bytes(info.InstalledBytes > mst.ullTotalPhys ? info.InstalledBytes - mst.ullTotalPhys : 0))
            .AddIf("Type", info.Type)
            .AddIf("Form factor", info.FormFactor)
            .AddIf("Speed", info.SpeedMTs > 0 ? $"{info.SpeedMTs} MT/s" : null)
            .Add("Slots used", info.SlotsTotal > 0 ? $"{info.SlotsUsed} of {info.SlotsTotal}" : info.SlotsUsed.ToString());
        long maxKb = arr.Sum(a => a.L("MaxCapacityEx"));
        if (maxKb == 0) maxKb = arr.Sum(a => a.L("MaxCapacity"));
        summary.AddIf("Max supported capacity", maxKb > 0 ? Fmt.Bytes(maxKb * 1024.0, 0) : null);
        var ecc = arr.FirstOrDefault()?.L("MemoryErrorCorrection") ?? 0;
        summary.AddIf("Error correction", ecc switch { 3 => "None", 4 => "Parity", 5 => "Single-bit ECC", 6 => "Multi-bit ECC", 7 => "CRC", _ => null });
        info.Sections.Add(summary);

        var pf = Wmi.Query("SELECT * FROM Win32_PageFileUsage");
        if (pf.Count > 0)
        {
            var s = new InfoSection("Page files");
            foreach (var p in pf)
                s.Add(p.S("Name"), $"{p.L("CurrentUsage")} MB used of {p.L("AllocatedBaseSize")} MB (peak {p.L("PeakUsage")} MB)");
            info.Sections.Add(s);
        }

        var virt = new InfoSection("Virtual memory");
        virt.Add("Commit limit", Fmt.Bytes(mst.ullTotalPageFile))
            .Add("Total virtual address space", Fmt.Bytes(mst.ullTotalVirtual))
            .Add("Page size", Fmt.Bytes((double)Native.PerformanceInfo().PageSize, 0));
        info.Sections.Add(virt);
        return info;
    }

    // ------------------------------------------------------------------ Storage
    private static string BusName(long b) => b switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File-backed virtual",
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => "Unknown",
    };

    private static string MediaName(long m) => m switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };

    private static string HealthName(long h) => h switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };

    private static string PartitionTypeName(string gpt, string mbrType)
    {
        return gpt.ToLowerInvariant().Trim('{', '}') switch
        {
            "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EFI system",
            "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Microsoft reserved",
            "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Basic data",
            "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Recovery",
            "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3" => "LDM metadata",
            "af9b60a0-1431-4f62-bc68-3311714a69ad" => "LDM data",
            "e75caf8f-f680-4cee-afa3-b001e56efc2d" => "Storage Spaces",
            "0fc63daf-8483-4772-8e79-3d69d8477de4" => "Linux filesystem",
            "" => string.IsNullOrEmpty(mbrType) ? "Partition" : mbrType,
            _ => "Other",
        };
    }

    public static List<DiskInfo> Storage()
    {
        const string ns = @"root\Microsoft\Windows\Storage";
        var result = new List<DiskInfo>();
        var physical = Wmi.Query("SELECT * FROM MSFT_PhysicalDisk", ns);
        var disks = Wmi.Query("SELECT * FROM MSFT_Disk", ns);
        var parts = Wmi.Query("SELECT * FROM MSFT_Partition", ns);
        var reliability = IsAdmin() ? Wmi.Query("SELECT * FROM MSFT_StorageReliabilityCounter", ns) : new List<ManagementBaseObject>();
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToDictionary(d => d.Name[0], d => d);

        foreach (var pd in physical)
        {
            int number = int.TryParse(pd.S("DeviceId"), out var n) ? n : -1;
            var disk = disks.FirstOrDefault(d => d.L("Number") == number);
            var info = new DiskInfo
            {
                Number = number,
                Model = pd.S("FriendlyName"),
                Media = MediaName(pd.L("MediaType")),
                Bus = BusName(pd.L("BusType")),
                SizeBytes = pd.U("Size"),
                Health = HealthName(pd.L("HealthStatus")),
                IsSystem = disk?.B("IsSystem") == true || disk?.B("IsBoot") == true,
            };
            if (info.Media == "" && info.Bus == "NVMe") info.Media = "SSD";

            var s = new InfoSection("Drive");
            s.Add("Model", info.Model)
             .Add("Type", $"{(info.Media == "" ? "Disk" : info.Media)} ({info.Bus})")
             .Add("Capacity", $"{Fmt.Bytes(info.SizeBytes)} ({Fmt.BytesDecimal(info.SizeBytes)})")
             .Add("Health status", info.Health)
             .AddIf("Operational status", OpStatus(pd.V("OperationalStatus")))
             .AddIf("Manufacturer", pd.S("Manufacturer") is var mfr && !mfr.Equals("NVMe", StringComparison.OrdinalIgnoreCase) && !mfr.Equals("(Standard disk drives)", StringComparison.OrdinalIgnoreCase) ? mfr : null)
             .AddIf("Serial number", pd.S("SerialNumber").TrimEnd('.', ' '))
             .AddIf("Firmware", pd.S("FirmwareVersion"))
             .AddIf("Disk number", number >= 0 ? number.ToString() : null)
             .AddIf("Spindle speed", pd.L("SpindleSpeed") is > 0 and < 100000 ? $"{pd.L("SpindleSpeed")} RPM" : null)
             .AddIf("Sector size", pd.L("LogicalSectorSize") > 0 ? $"{pd.L("LogicalSectorSize")} B logical / {pd.L("PhysicalSectorSize")} B physical" : null);
            if (disk != null)
            {
                s.Add("Partition style", disk.L("PartitionStyle") switch { 1 => "MBR", 2 => "GPT", _ => "RAW" })
                 .Add("Boot / system disk", Fmt.YesNo(info.IsSystem))
                 .AddIf("Location", disk.S("Location"));
            }
            info.Sections.Add(s);

            var rel = reliability.FirstOrDefault(r => r.S("DeviceId") == pd.S("DeviceId"));
            if (rel != null)
            {
                var r = new InfoSection("Reliability (SMART)");
                r.AddIf("Temperature", rel.L("Temperature") > 0 ? Fmt.Temp(rel.L("Temperature")) : null)
                 .AddIf("Max temperature", rel.L("TemperatureMax") > 0 ? Fmt.Temp(rel.L("TemperatureMax")) : null)
                 .AddIf("Wear", rel.V("Wear") != null ? $"{rel.L("Wear")}%" : null)
                 .AddIf("Power-on hours", rel.V("PowerOnHours") != null ? $"{rel.L("PowerOnHours"):N0} h" : null)
                 .AddIf("Start/stop cycles", rel.V("StartStopCycleCount") != null ? rel.L("StartStopCycleCount").ToString("N0") : null)
                 .AddIf("Read errors", rel.V("ReadErrorsTotal") != null ? rel.L("ReadErrorsTotal").ToString("N0") : null)
                 .AddIf("Write errors", rel.V("WriteErrorsTotal") != null ? rel.L("WriteErrorsTotal").ToString("N0") : null);
                if (r.Items.Count > 0) info.Sections.Add(r);
            }

            var p = new InfoSection("Partitions");
            foreach (var part in parts.Where(x => x.L("DiskNumber") == number).OrderBy(x => x.L("Offset")))
            {
                char letter = (char)part.L("DriveLetter");
                string name = PartitionTypeName(part.S("GptType"), part.S("Type"));
                string label = char.IsLetter(letter) ? $"{letter}:  {name}" : name;
                string detail = Fmt.Bytes(part.U("Size"));
                if (char.IsLetter(letter))
                {
                    info.Letters.Add($"{letter}:");
                    if (drives.TryGetValue(letter, out var di))
                    {
                        try { detail += $" · {di.DriveFormat} · {Fmt.Bytes(di.AvailableFreeSpace)} free"; } catch { }
                    }
                }
                if (part.B("IsBoot") == true) detail += " · boot";
                if (part.B("IsSystem") == true) detail += " · system";
                p.Add($"#{part.L("PartitionNumber")}  {label}", detail);
            }
            if (p.Items.Count > 0) info.Sections.Add(p);
            result.Add(info);
        }

        // Fallback to Win32_DiskDrive when the storage namespace is unavailable.
        if (result.Count == 0)
        {
            foreach (var d in Wmi.Query("SELECT * FROM Win32_DiskDrive"))
            {
                var info = new DiskInfo { Number = (int)d.L("Index"), Model = d.S("Model"), Bus = d.S("InterfaceType"), SizeBytes = d.U("Size"), Health = d.S("Status") };
                var s = new InfoSection("Drive");
                s.Add("Model", info.Model).Add("Interface", info.Bus).Add("Capacity", Fmt.Bytes(info.SizeBytes))
                 .AddIf("Serial number", d.S("SerialNumber")).AddIf("Firmware", d.S("FirmwareRevision")).Add("Partitions", d.L("Partitions").ToString());
                info.Sections.Add(s);
                result.Add(info);
            }
        }
        return result.OrderBy(d => d.Number).ToList();
    }

    private static string? OpStatus(object? v)
    {
        if (v is not ushort[] arr || arr.Length == 0) return null;
        return string.Join(", ", arr.Select(x => x switch { 2 => "OK", 3 => "Degraded", 5 => "Predictive failure", 6 => "Error", 10 => "Stopped", 0xD00D => "Online", _ => x.ToString() }));
    }

    // ------------------------------------------------------------------ Motherboard / BIOS
    private static string Chassis(long t) => t switch
    {
        3 => "Desktop", 4 => "Low-profile desktop", 5 => "Pizza box", 6 => "Mini tower", 7 => "Tower", 8 => "Portable", 9 => "Laptop",
        10 => "Notebook", 11 => "Handheld", 12 => "Docking station", 13 => "All-in-one", 14 => "Sub-notebook", 15 => "Space-saving",
        16 => "Lunch box", 17 => "Main server chassis", 23 => "Rack mount", 24 => "Sealed-case PC", 30 => "Tablet", 31 => "Convertible",
        32 => "Detachable", 33 => "IoT gateway", 34 => "Embedded PC", 35 => "Mini PC", 36 => "Stick PC", _ => $"Type {t}",
    };

    public static List<InfoSection> Board(out string summary)
    {
        var list = new List<InfoSection>();
        var cs = Wmi.First("SELECT * FROM Win32_ComputerSystem");
        var csp = Wmi.First("SELECT * FROM Win32_ComputerSystemProduct");
        var bb = Wmi.First("SELECT * FROM Win32_BaseBoard");
        var bios = Wmi.First("SELECT * FROM Win32_BIOS");
        var enc = Wmi.First("SELECT * FROM Win32_SystemEnclosure");

        var sys = new InfoSection("System");
        if (cs != null)
        {
            sys.Add("Manufacturer", cs.S("Manufacturer"))
               .Add("Model", cs.S("Model"))
               .AddIf("Family", cs.S("SystemFamily"))
               .AddIf("SKU", cs.S("SystemSKUNumber"))
               .Add("PC type", cs.L("PCSystemType") switch { 1 => "Desktop", 2 => "Mobile", 3 => "Workstation", 4 => "Enterprise server", 5 => "SOHO server", 6 => "Appliance PC", 7 => "Performance server", 8 => "Slate", _ => Fmt.Dash });
        }
        if (enc?.V("ChassisTypes") is ushort[] ct && ct.Length > 0) sys.Add("Chassis", Chassis(ct[0]));
        if (csp != null)
        {
            sys.AddIf("Product version", csp.S("Version"))
               .AddIf("Serial number", csp.S("IdentifyingNumber"))
               .AddIf("UUID", csp.S("UUID"));
        }
        list.Add(sys);

        var board = new InfoSection("Motherboard");
        if (bb != null)
        {
            board.Add("Manufacturer", bb.S("Manufacturer"))
                 .Add("Product", bb.S("Product"))
                 .AddIf("Version", bb.S("Version"))
                 .AddIf("Serial number", bb.S("SerialNumber"));
        }
        list.Add(board);

        var fw = new InfoSection("BIOS / UEFI");
        if (bios != null)
        {
            fw.Add("Vendor", bios.S("Manufacturer"))
              .Add("Version", bios.S("SMBIOSBIOSVersion"))
              .Add("Release date", Fmt.Date(bios.D("ReleaseDate")))
              .AddIf("BIOS revision", bios.L("SystemBiosMajorVersion") is > 0 and < 255 ? $"{bios.L("SystemBiosMajorVersion")}.{bios.L("SystemBiosMinorVersion")}" : null)
              .AddIf("Embedded controller", bios.L("EmbeddedControllerMajorVersion") is > 0 and < 255 ? $"{bios.L("EmbeddedControllerMajorVersion")}.{bios.L("EmbeddedControllerMinorVersion")}" : null)
              .AddIf("SMBIOS version", $"{bios.L("SMBIOSMajorVersion")}.{bios.L("SMBIOSMinorVersion")}");
        }
        fw.Add("Firmware type", Native.GetFirmwareType(out uint ft) ? ft switch { 1 => "Legacy BIOS", 2 => "UEFI", _ => "Unknown" } : Fmt.Dash)
          .Add("Secure Boot", SecureBoot());
        list.Add(fw);

        summary = cs == null ? "" : $"{cs.S("Manufacturer")} {cs.S("Model")}".Trim();
        return list;
    }

    public static string SecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is int v ? (v == 1 ? "Enabled" : "Disabled") : "Not supported";
        }
        catch { return Fmt.Dash; }
    }

    // ------------------------------------------------------------------ Operating system
    public static List<InfoSection> OperatingSystem(out string summary)
    {
        var list = new List<InfoSection>();
        var os = Wmi.First("SELECT * FROM Win32_OperatingSystem");
        var cs = Wmi.First("SELECT * FROM Win32_ComputerSystem");
        string displayVersion = "", ubr = "", edition = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            displayVersion = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
            ubr = key?.GetValue("UBR")?.ToString() ?? "";
            edition = key?.GetValue("EditionID") as string ?? "";
        }
        catch { }

        var win = new InfoSection("Windows");
        string caption = os?.S("Caption").Replace("Microsoft ", "") ?? RuntimeInformation.OSDescription;
        string build = os?.S("BuildNumber") ?? Environment.OSVersion.Version.Build.ToString();
        win.Add("Edition", caption)
           .AddIf("Version", displayVersion)
           .Add("OS build", string.IsNullOrEmpty(ubr) ? build : $"{build}.{ubr}")
           .AddIf("Edition ID", edition)
           .Add("Architecture", os?.S("OSArchitecture") ?? RuntimeInformation.OSArchitecture.ToString());
        if (os != null)
        {
            win.Add("Installed on", Fmt.Stamp(os.D("InstallDate")))
               .Add("Last boot", Fmt.Stamp(os.D("LastBootUpTime")))
               .AddIf("Registered owner", os.S("RegisteredUser"))
               .AddIf("Organization", os.S("Organization"))
               .AddIf("Product ID", os.S("SerialNumber"))
               .Add("Windows directory", os.S("WindowsDirectory"))
               .Add("System drive", os.S("SystemDrive"));
        }
        list.Add(win);

        var comp = new InfoSection("Computer");
        comp.Add("Device name", Environment.MachineName)
            .Add("User", $"{Environment.UserDomainName}\\{Environment.UserName}");
        if (cs != null)
        {
            comp.Add(cs.B("PartOfDomain") == true ? "Domain" : "Workgroup", cs.B("PartOfDomain") == true ? cs.S("Domain") : cs.S("Workgroup"))
                .Add("Hypervisor present", Fmt.YesNo(cs.B("HypervisorPresent")));
        }
        comp.Add("Time zone", TimeZoneInfo.Local.DisplayName)
            .Add("Locale", System.Globalization.CultureInfo.CurrentCulture.DisplayName)
            .Add("UI language", System.Globalization.CultureInfo.CurrentUICulture.DisplayName)
            .AddIf("Active power plan", PowerPlan());
        list.Add(comp);

        var sec = new InfoSection("Security");
        sec.Add("Secure Boot", SecureBoot());
        var dg = Wmi.First("SELECT * FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard");
        if (dg != null)
        {
            sec.Add("Virtualization-based security", dg.L("VirtualizationBasedSecurityStatus") switch { 0 => "Off", 1 => "Enabled, not running", 2 => "Running", _ => Fmt.Dash });
            var running = dg.V("SecurityServicesRunning") as uint[] ?? Array.Empty<uint>();
            sec.Add("Memory integrity (HVCI)", running.Contains(2u) ? "Running" : "Off")
               .Add("Credential Guard", running.Contains(1u) ? "Running" : "Off");
        }
        if (IsAdmin())
        {
            var tpm = Wmi.First("SELECT * FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm");
            sec.Add("TPM", tpm == null ? "Not found" : $"{(tpm.B("IsEnabled_InitialValue") == true ? "Enabled" : "Present")} · spec {tpm.S("SpecVersion").Split(',').FirstOrDefault()}");
            if (tpm != null) sec.AddIf("TPM manufacturer", tpm.S("ManufacturerIdTxt") + " " + tpm.S("ManufacturerVersion"));
        }
        else
        {
            sec.Add("TPM", "Run as administrator to read");
        }
        var mp = Wmi.First("SELECT * FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender");
        if (mp != null)
        {
            sec.Add("Microsoft Defender antivirus", Fmt.OnOff(mp.B("AntivirusEnabled")))
               .Add("Real-time protection", Fmt.OnOff(mp.B("RealTimeProtectionEnabled")))
               .AddIf("Tamper protection", Fmt.OnOff(mp.B("IsTamperProtected")))
               .AddIf("Signature version", mp.S("AntivirusSignatureVersion"))
               .AddIf("Signatures updated", Fmt.Stamp(mp.D("AntivirusSignatureLastUpdated")));
        }
        sec.Add("CPUx running elevated", Fmt.YesNo(IsAdmin()));
        list.Add(sec);

        var rt = new InfoSection("Runtime");
        rt.Add(".NET runtime", RuntimeInformation.FrameworkDescription)
          .Add("Process architecture", RuntimeInformation.ProcessArchitecture.ToString())
          .Add("CPUx version", typeof(SystemInfoReader).Assembly.GetName().Version?.ToString(3) ?? "1.0.0")
          .Add("Settings & logs", Log.Directory);
        list.Add(rt);

        summary = string.IsNullOrEmpty(displayVersion) ? caption : $"{caption} {displayVersion}";
        return list;
    }

    private static string? PowerPlan()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            int a = output.LastIndexOf('('), b = output.LastIndexOf(')');
            return a >= 0 && b > a ? output[(a + 1)..b] : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ Battery
    public static BatteryInfo Battery()
    {
        var info = new BatteryInfo();
        try
        {
            var devices = BatteryDevice.Open();
            var dev = devices.FirstOrDefault();
            if (dev == null) return info;
            info.Present = true;
            var bi = dev.Information();
            string name = dev.Text(4), maker = dev.Text(6), serial = dev.Text(8);
            info.Name = string.IsNullOrEmpty(name) ? "Battery" : name;
            var s = new InfoSection("Battery");
            s.Add("Name", info.Name)
             .AddIf("Manufacturer", maker)
             .AddIf("Serial number", serial)
             .AddIf("Manufacture date", Fmt.Date(dev.ManufactureDate()) is var md && md != Fmt.Dash ? md : null);
            if (bi is { } b)
            {
                info.DesignWh = b.DesignedCapacity > 0 && b.DesignedCapacity != uint.MaxValue ? b.DesignedCapacity / 1000.0 : double.NaN;
                info.FullWh = b.FullChargedCapacity > 0 && b.FullChargedCapacity != uint.MaxValue ? b.FullChargedCapacity / 1000.0 : double.NaN;
                info.CycleCount = (int)b.CycleCount;
                if (info.CycleCount <= 0)
                {
                    var cc = Wmi.First("SELECT CycleCount FROM BatteryCycleCount", @"root\wmi");
                    if (cc != null && cc.L("CycleCount") > 0) info.CycleCount = (int)cc.L("CycleCount");
                }
                bool relative = (b.Capabilities & 0x40000000) != 0;
                string unit = relative ? "" : " Wh";
                s.Add("Chemistry", BatteryDevice.Chemistry(b.Chemistry))
                 .Add("Rechargeable", Fmt.YesNo(b.Technology == 1))
                 .Add("Design capacity", double.IsNaN(info.DesignWh) ? Fmt.Dash : $"{info.DesignWh:0.0}{unit}")
                 .Add("Full charge capacity", double.IsNaN(info.FullWh) ? Fmt.Dash : $"{info.FullWh:0.0}{unit}")
                 .Add("Wear level", double.IsNaN(info.WearPercent) ? Fmt.Dash : $"{info.WearPercent:0.0}%")
                 .Add("Health", double.IsNaN(info.WearPercent) ? Fmt.Dash : $"{100 - info.WearPercent:0.0}%")
                 .Add("Cycle count", info.CycleCount > 0 ? info.CycleCount.ToString() : "Not reported");
                var wb = Wmi.First("SELECT DesignVoltage FROM Win32_Battery");
                if (wb != null && wb.L("DesignVoltage") > 0) s.Add("Design voltage", $"{wb.L("DesignVoltage") / 1000.0:0.00} V");
            }
            info.Sections.Add(s);
            foreach (var d in devices) d.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Battery info", ex);
        }
        return info;
    }

    // ------------------------------------------------------------------ Devices
    private static readonly Dictionary<string, string> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AudioEndpoint"] = "Audio endpoints", ["MEDIA"] = "Sound, video & game controllers", ["Bluetooth"] = "Bluetooth",
        ["Camera"] = "Cameras", ["Image"] = "Imaging devices", ["Computer"] = "Computer", ["DiskDrive"] = "Disk drives",
        ["Display"] = "Display adapters", ["Firmware"] = "Firmware", ["HIDClass"] = "Human interface devices", ["Keyboard"] = "Keyboards",
        ["Monitor"] = "Monitors", ["Mouse"] = "Mice & pointing devices", ["Net"] = "Network adapters", ["Ports"] = "Ports (COM & LPT)",
        ["PrintQueue"] = "Print queues", ["Printer"] = "Printers", ["Processor"] = "Processors", ["SCSIAdapter"] = "Storage controllers",
        ["HDC"] = "IDE / SATA controllers", ["SecurityDevices"] = "Security devices", ["SoftwareComponent"] = "Software components",
        ["SoftwareDevice"] = "Software devices", ["System"] = "System devices", ["USB"] = "USB controllers", ["USBDevice"] = "USB devices",
        ["Volume"] = "Storage volumes", ["VolumeSnapshot"] = "Volume shadow copies", ["WPD"] = "Portable devices", ["Battery"] = "Batteries",
        ["Biometric"] = "Biometric devices", ["Sensor"] = "Sensors", ["SmartCardReader"] = "Smart card readers", ["Extension"] = "Extensions",
        ["DigitalMediaDevices"] = "Digital media devices", ["Infrared"] = "Infrared devices", ["MTD"] = "Memory technology devices",
        ["SDHost"] = "SD host adapters", ["UCM"] = "USB connector managers", ["Net Client"] = "Network clients",
        ["AudioProcessingObject"] = "Audio processing objects", ["ComputeAccelerator"] = "Neural processors", ["Media"] = "Sound, video & game controllers",
        ["LiteOnSensor"] = "Sensors", ["Modem"] = "Modems", ["MultiFunction"] = "Multifunction adapters", ["NetService"] = "Network services",
        ["NetTrans"] = "Network protocols", ["PCMCIA"] = "PCMCIA adapters", ["PnpPrinters"] = "Printers", ["WSDPrintDevice"] = "Printers",
        ["XboxComposite"] = "Xbox peripherals", ["Proximity"] = "Proximity devices",
    };

    public static string ClassName(string pnpClass) =>
        string.IsNullOrWhiteSpace(pnpClass) ? "Other devices" : ClassNames.TryGetValue(pnpClass, out var n) ? n : pnpClass;

    public static List<(string Class, List<DeviceEntry> Devices)> Devices()
    {
        var list = Wmi.Query("SELECT Name, PNPClass, Manufacturer, Status, ConfigManagerErrorCode, DeviceID, Service, Present FROM Win32_PnPEntity");
        return list
            .Where(d => d.B("Present") != false)
            .Select(d => (Class: ClassName(d.S("PNPClass")), Entry: new DeviceEntry
            {
                Name = string.IsNullOrEmpty(d.S("Name")) ? d.S("DeviceID") : d.S("Name"),
                Manufacturer = d.S("Manufacturer"),
                Status = d.S("Status"),
                ErrorCode = (int)d.L("ConfigManagerErrorCode"),
                DeviceId = d.S("DeviceID"),
                Driver = d.S("Service"),
            }))
            .GroupBy(x => x.Class)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Select(x => x.Entry).OrderBy(e => e.Name).ToList()))
            .ToList();
    }
}
