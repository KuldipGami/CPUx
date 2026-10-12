using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using CPUx.Core;
using Microsoft.Win32;

namespace CPUx.Services;

public sealed class CpuInfo
{
    public string Name { get; set; } = "Processor";
    public string Vendor { get; set; } = "";
    public string Codename { get; set; } = "";
    public string Socket { get; set; } = "";
    public int Packages { get; set; } = 1;
    public int Cores { get; set; }
    public int Threads { get; set; } = Environment.ProcessorCount;
    public int PerformanceCores { get; set; }
    public int EfficiencyCores { get; set; }
    public bool Hybrid => PerformanceCores > 0 && EfficiencyCores > 0;
    public double BaseMHz { get; set; }
    public string L1 { get; set; } = "";
    public string L2 { get; set; } = "";
    public string L3 { get; set; } = "";
    public bool? Virtualization { get; set; }

    /// <summary>Physical core index for each logical processor (group 0 numbering).</summary>
    public int[] CoreOfLogical { get; set; } = Array.Empty<int>();
    /// <summary>True when the logical processor sits on an efficiency core.</summary>
    public bool[] LogicalIsEfficiency { get; set; } = Array.Empty<bool>();

    public List<(string Name, bool Supported)> InstructionSets { get; } = new();
    public List<InfoSection> Sections { get; } = new();
}

public static class CpuInfoReader
{
    private record CacheGroup(int Level, int Type, uint Size, int Assoc, int Line, int Count);

    public static CpuInfo Read()
    {
        var info = new CpuInfo();
        try { ReadTopology(info); } catch (Exception ex) { Log.Error("CPU topology", ex); }

        var wmi = Wmi.Query("SELECT * FROM Win32_Processor");
        var p = wmi.FirstOrDefault();
        if (p != null)
        {
            info.Name = Clean(p.S("Name"));
            info.Vendor = p.S("Manufacturer");
            info.Socket = p.S("SocketDesignation");
            info.BaseMHz = p.L("MaxClockSpeed");
            info.Packages = Math.Max(1, wmi.Count);
            if (info.Cores == 0) info.Cores = (int)wmi.Sum(x => x.L("NumberOfCores"));
            info.Threads = (int)Math.Max(info.Threads, wmi.Sum(x => x.L("NumberOfLogicalProcessors")));
            info.Virtualization = p.B("VirtualizationFirmwareEnabled");
        }

        // CPUID gives exact brand string, family/model/stepping and feature flags.
        string family = "", model = "", stepping = "", extFamily = "", extModel = "", cpuidVendor = "";
        if (X86Base.IsSupported)
        {
            try
            {
                var (_, b0, c0, d0) = X86Base.CpuId(0, 0);
                cpuidVendor = Ascii(b0) + Ascii(d0) + Ascii(c0);
                var (a1, _, _, _) = X86Base.CpuId(1, 0);
                int step = a1 & 0xF, mod = (a1 >> 4) & 0xF, fam = (a1 >> 8) & 0xF, eMod = (a1 >> 16) & 0xF, eFam = (a1 >> 20) & 0xFF;
                int dispFam = fam == 0xF ? fam + eFam : fam;
                int dispMod = fam is 0x6 or 0xF ? (eMod << 4) + mod : mod;
                family = $"{dispFam:X}h ({dispFam})";
                model = $"{dispMod:X2}h ({dispMod})";
                stepping = step.ToString();
                extFamily = eFam.ToString("X");
                extModel = eMod.ToString("X");
                info.Codename = Codename(cpuidVendor, dispFam, dispMod);
                string brand = BrandString();
                if (!string.IsNullOrWhiteSpace(brand)) info.Name = Clean(brand);
                ReadFeatures(info);
            }
            catch (Exception ex) { Log.Error("CPUID", ex); }
        }
        else
        {
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                info.InstructionSets.Add(("ARMv8 AdvSIMD (NEON)", System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported));
                info.InstructionSets.Add(("AES", System.Runtime.Intrinsics.Arm.Aes.IsSupported));
                info.InstructionSets.Add(("CRC32", System.Runtime.Intrinsics.Arm.Crc32.IsSupported));
                info.InstructionSets.Add(("SHA1", System.Runtime.Intrinsics.Arm.Sha1.IsSupported));
                info.InstructionSets.Add(("SHA256", System.Runtime.Intrinsics.Arm.Sha256.IsSupported));
                info.InstructionSets.Add(("DotProd", System.Runtime.Intrinsics.Arm.Dp.IsSupported));
                info.InstructionSets.Add(("RDM", System.Runtime.Intrinsics.Arm.Rdm.IsSupported));
            }
        }

        // ---- Sections
        var proc = new InfoSection("Processor");
        proc.Add("Name", info.Name)
            .Add("Manufacturer", VendorName(info.Vendor, cpuidVendor))
            .AddIf("Codename", info.Codename)
            .Add("Architecture", RuntimeInformation.OSArchitecture.ToString())
            .Add("Socket", info.Socket)
            .Add("Base clock", Fmt.MHz(info.BaseMHz));
        if (p != null)
        {
            proc.AddIf("Max clock (WMI)", Fmt.MHz(p.L("MaxClockSpeed")))
                .AddIf("External clock", p.L("ExtClock") > 0 ? $"{p.L("ExtClock")} MHz" : null)
                .AddIf("Processor ID", p.S("ProcessorId"))
                .AddIf("Revision", p.S("Revision"))
                .AddIf("Caption", p.S("Caption"))
                .AddIf("Status", p.S("Status"));
        }
        info.Sections.Add(proc);

        var topo = new InfoSection("Topology");
        topo.Add("Sockets", info.Packages.ToString())
            .Add("Cores", info.Cores.ToString())
            .Add("Logical processors", info.Threads.ToString());
        if (info.Hybrid)
        {
            topo.Add("Performance cores", info.PerformanceCores.ToString())
                .Add("Efficiency cores", info.EfficiencyCores.ToString())
                .Add("Hybrid architecture", "Yes");
        }
        topo.Add("Hyper-threading / SMT", Fmt.YesNo(info.Threads > info.Cores))
            .Add("Virtualization (firmware)", Fmt.OnOff(info.Virtualization));
        if (p != null)
        {
            topo.AddIf("VM monitor extensions", Fmt.YesNo(p.B("VMMonitorModeExtensions")))
                .AddIf("SLAT", Fmt.YesNo(p.B("SecondLevelAddressTranslationExtensions")));
        }
        info.Sections.Add(topo);

        var cache = new InfoSection("Cache");
        try { FillCaches(info, cache); } catch (Exception ex) { Log.Error("CPU cache", ex); }
        if (cache.Items.Count > 0) info.Sections.Add(cache);

        if (!string.IsNullOrEmpty(family))
        {
            var id = new InfoSection("CPUID");
            id.Add("Vendor string", cpuidVendor)
              .Add("Family", family)
              .Add("Model", model)
              .Add("Stepping", stepping)
              .Add("Extended family", extFamily)
              .Add("Extended model", extModel);
            var (microcode, identifier) = RegistryCpu();
            id.AddIf("Microcode revision", microcode);
            id.AddIf("Identifier", identifier);
            info.Sections.Add(id);
        }
        return info;
    }

    private static string Clean(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static string Ascii(int v) =>
        new string(new[] { (char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 24) & 0xFF) });

    private static string BrandString()
    {
        var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        if ((uint)maxExt < 0x80000004) return "";
        var sb = new System.Text.StringBuilder();
        for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
        {
            var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), 0);
            sb.Append(Ascii(a)).Append(Ascii(b)).Append(Ascii(c)).Append(Ascii(d));
        }
        return sb.ToString().Replace("\0", "").Trim();
    }

    private static string VendorName(string wmiVendor, string cpuidVendor)
    {
        string v = string.IsNullOrEmpty(cpuidVendor) ? wmiVendor : cpuidVendor;
        return v switch
        {
            "GenuineIntel" => "Intel",
            "AuthenticAMD" => "AMD",
            _ => string.IsNullOrEmpty(wmiVendor) ? v : wmiVendor,
        };
    }

    private static string Codename(string vendor, int family, int model)
    {
        if (vendor == "GenuineIntel" && family == 6)
        {
            return model switch
            {
                0x3C or 0x3F or 0x45 or 0x46 => "Haswell",
                0x3D or 0x47 or 0x4F or 0x56 => "Broadwell",
                0x4E or 0x5E => "Skylake",
                0x55 => "Skylake-SP / Cascade Lake",
                0x8E or 0x9E => "Kaby Lake / Coffee Lake",
                0x66 => "Cannon Lake",
                0x7D or 0x7E => "Ice Lake",
                0x8C or 0x8D => "Tiger Lake",
                0xA5 or 0xA6 => "Comet Lake",
                0xA7 => "Rocket Lake",
                0x97 or 0x9A => "Alder Lake",
                0xBE => "Alder Lake-N",
                0xB7 or 0xBA or 0xBF => "Raptor Lake",
                0xAA or 0xAC => "Meteor Lake",
                0xBD => "Lunar Lake",
                0xC5 or 0xC6 => "Arrow Lake",
                0x8F => "Sapphire Rapids",
                0xCF => "Emerald Rapids",
                _ => "",
            };
        }
        if (vendor == "AuthenticAMD")
        {
            return (family, model) switch
            {
                (0x17, 0x01) or (0x17, 0x08) => "Zen / Zen+ (Summit / Pinnacle Ridge)",
                (0x17, 0x11) or (0x17, 0x18) => "Zen (Raven Ridge / Picasso)",
                (0x17, 0x31) => "Zen 2 (Rome / Castle Peak)",
                (0x17, 0x60) => "Zen 2 (Renoir)",
                (0x17, 0x68) => "Zen 2 (Lucienne)",
                (0x17, 0x71) => "Zen 2 (Matisse)",
                (0x17, 0x90) => "Zen 2 (Van Gogh)",
                (0x17, 0xA0) => "Zen 2 (Mendocino)",
                (0x19, 0x01) => "Zen 3 (Milan)",
                (0x19, 0x21) => "Zen 3 (Vermeer)",
                (0x19, 0x50) => "Zen 3 (Cezanne)",
                (0x19, 0x44) => "Zen 3+ (Rembrandt)",
                (0x19, 0x11) => "Zen 4 (Genoa)",
                (0x19, 0x61) => "Zen 4 (Raphael)",
                (0x19, 0x74) or (0x19, 0x75) => "Zen 4 (Phoenix / Hawk Point)",
                (0x1A, 0x44) => "Zen 5 (Granite Ridge)",
                (0x1A, 0x24) => "Zen 5 (Strix Point)",
                _ => family switch { 0x17 => "Zen / Zen 2", 0x19 => "Zen 3 / Zen 4", 0x1A => "Zen 5", _ => "" },
            };
        }
        return "";
    }

    private static void ReadFeatures(CpuInfo info)
    {
        var (_, _, c1, d1) = X86Base.CpuId(1, 0);
        var (max, _, _, _) = X86Base.CpuId(0, 0);
        int b7 = 0, c7 = 0, d7 = 0, a71 = 0;
        if (max >= 7)
        {
            (_, b7, c7, d7) = X86Base.CpuId(7, 0);
            (a71, _, _, _) = X86Base.CpuId(7, 1);
        }
        var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        int ce = 0, de = 0;
        if ((uint)maxExt >= 0x80000001) (_, _, ce, de) = X86Base.CpuId(unchecked((int)0x80000001), 0);

        static bool Bit(int reg, int bit) => (reg & (1 << bit)) != 0;
        var list = info.InstructionSets;
        list.Add(("MMX", Bit(d1, 23)));
        list.Add(("SSE", Bit(d1, 25)));
        list.Add(("SSE2", Bit(d1, 26)));
        list.Add(("SSE3", Bit(c1, 0)));
        list.Add(("SSSE3", Bit(c1, 9)));
        list.Add(("SSE4.1", Bit(c1, 19)));
        list.Add(("SSE4.2", Bit(c1, 20)));
        list.Add(("SSE4a", Bit(ce, 6)));
        list.Add(("AVX", Bit(c1, 28)));
        list.Add(("AVX2", Bit(b7, 5)));
        list.Add(("AVX-VNNI", Bit(a71, 4)));
        list.Add(("AVX-512F", Bit(b7, 16)));
        list.Add(("AVX-512DQ", Bit(b7, 17)));
        list.Add(("AVX-512CD", Bit(b7, 28)));
        list.Add(("AVX-512BW", Bit(b7, 30)));
        list.Add(("AVX-512VL", Bit(b7, 31)));
        list.Add(("AVX-512 VNNI", Bit(c7, 11)));
        list.Add(("AVX-512 BF16", Bit(a71, 5)));
        list.Add(("FMA3", Bit(c1, 12)));
        list.Add(("FMA4", Bit(ce, 16)));
        list.Add(("F16C", Bit(c1, 29)));
        list.Add(("AES-NI", Bit(c1, 25)));
        list.Add(("VAES", Bit(c7, 9)));
        list.Add(("PCLMULQDQ", Bit(c1, 1)));
        list.Add(("SHA", Bit(b7, 29)));
        list.Add(("GFNI", Bit(c7, 8)));
        list.Add(("BMI1", Bit(b7, 3)));
        list.Add(("BMI2", Bit(b7, 8)));
        list.Add(("ADX", Bit(b7, 19)));
        list.Add(("POPCNT", Bit(c1, 23)));
        list.Add(("LZCNT", Bit(ce, 5)));
        list.Add(("MOVBE", Bit(c1, 22)));
        list.Add(("RDRAND", Bit(c1, 30)));
        list.Add(("RDSEED", Bit(b7, 18)));
        list.Add(("x86-64", Bit(de, 29)));
        list.Add(("NX / XD", Bit(de, 20)));
        list.Add(("VT-x", Bit(c1, 5)));
        list.Add(("AMD-V", Bit(ce, 2)));
        list.Add(("HTT", Bit(d1, 28)));
        list.Add(("TSX", Bit(b7, 11)));
        list.Add(("SGX", Bit(b7, 2)));
        list.Add(("CET-SS", Bit(c7, 7)));
        list.Add(("RDTSCP", Bit(de, 27)));
        list.Add(("XSAVE", Bit(c1, 26)));
        // Drop vendor-specific flags the CPU doesn't have to keep the list relevant.
        list.RemoveAll(x => !x.Supported && (x.Name is "AMD-V" or "SSE4a" or "FMA4" or "VT-x" or "SGX" or "TSX"));
    }

    private static void ReadTopology(CpuInfo info)
    {
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(Native.LOGICAL_PROCESSOR_RELATIONSHIP.All, IntPtr.Zero, ref len);
        if (len == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(Native.LOGICAL_PROCESSOR_RELATIONSHIP.All, buf, ref len)) return;
            var cores = new List<(byte Eff, ulong Mask, int Group)>();
            int packages = 0;
            var caches = new List<(int Level, int Type, uint Size, int Assoc, int Line)>();
            int offset = 0;
            while (offset < len)
            {
                IntPtr rec = buf + offset;
                int rel = Marshal.ReadInt32(rec);
                int size = Marshal.ReadInt32(rec, 4);
                IntPtr body = rec + 8;
                switch (rel)
                {
                    case 0: // ProcessorCore
                        byte eff = Marshal.ReadByte(body, 1);
                        ulong mask = (ulong)Marshal.ReadInt64(body, 24);
                        int group = Marshal.ReadInt16(body, 32);
                        cores.Add((eff, mask, group));
                        break;
                    case 2: // Cache
                        int level = Marshal.ReadByte(body, 0);
                        int assoc = Marshal.ReadByte(body, 1);
                        int line = (ushort)Marshal.ReadInt16(body, 2);
                        uint csize = (uint)Marshal.ReadInt32(body, 4);
                        int type = Marshal.ReadInt32(body, 8);
                        caches.Add((level, type, csize, assoc, line));
                        break;
                    case 3: // Package
                        packages++;
                        break;
                }
                if (size <= 0) break;
                offset += size;
            }

            info.Cores = cores.Count;
            if (packages > 0) info.Packages = packages;
            byte maxEff = cores.Count == 0 ? (byte)0 : cores.Max(c => c.Eff);
            byte minEff = cores.Count == 0 ? (byte)0 : cores.Min(c => c.Eff);
            if (maxEff != minEff)
            {
                info.PerformanceCores = cores.Count(c => c.Eff == maxEff);
                info.EfficiencyCores = cores.Count - info.PerformanceCores;
            }

            int lpCount = Environment.ProcessorCount;
            var coreOf = new int[lpCount];
            var isEff = new bool[lpCount];
            for (int ci = 0; ci < cores.Count; ci++)
            {
                if (cores[ci].Group != 0) continue;
                for (int bit = 0; bit < 64; bit++)
                {
                    if ((cores[ci].Mask & (1UL << bit)) == 0 || bit >= lpCount) continue;
                    coreOf[bit] = ci;
                    isEff[bit] = maxEff != minEff && cores[ci].Eff != maxEff;
                }
            }
            info.CoreOfLogical = coreOf;
            info.LogicalIsEfficiency = isEff;

            _cacheGroups = caches
                .GroupBy(c => (c.Level, c.Type, c.Size, c.Assoc, c.Line))
                .Select(g => new CacheGroup(g.Key.Level, g.Key.Type, g.Key.Size, g.Key.Assoc, g.Key.Line, g.Count()))
                .OrderBy(g => g.Level).ThenBy(g => g.Type == 2 ? 0 : g.Type == 1 ? 1 : 2).ThenByDescending(g => g.Size)
                .ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static List<CacheGroup> _cacheGroups = new();

    private static void FillCaches(CpuInfo info, InfoSection section)
    {
        string TypeName(int t) => t switch { 1 => "Instruction", 2 => "Data", 3 => "Trace", _ => "" };
        string Describe(IEnumerable<CacheGroup> groups) =>
            string.Join(" + ", groups.Select(g => $"{g.Count} × {Fmt.Bytes(g.Size, 0)}"));

        foreach (var level in _cacheGroups.GroupBy(g => (g.Level, g.Type)))
        {
            string label = $"L{level.Key.Level} {TypeName(level.Key.Type)}".Trim();
            var first = level.First();
            string detail = Describe(level);
            string extra = first.Assoc == 0xFF ? "fully associative" : $"{first.Assoc}-way";
            section.Add(label, $"{detail}  ({extra}, {first.Line}-byte line)");
        }
        long total(int lvl) => _cacheGroups.Where(g => g.Level == lvl).Sum(g => (long)g.Size * g.Count);
        info.L1 = total(1) > 0 ? Fmt.Bytes(total(1), 0) : "";
        info.L2 = total(2) > 0 ? Fmt.Bytes(total(2), 0) : "";
        info.L3 = total(3) > 0 ? Fmt.Bytes(total(3), 0) : "";
        if (total(1) > 0) section.Add("Total L1", info.L1);
        if (total(2) > 0) section.Add("Total L2", info.L2);
        if (total(3) > 0) section.Add("Total L3", info.L3);
    }

    private static (string? Microcode, string? Identifier) RegistryCpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key == null) return (null, null);
            string? micro = null;
            if (key.GetValue("Update Revision") is byte[] rev && rev.Length >= 8)
            {
                uint hi = BitConverter.ToUInt32(rev, 4), lo = BitConverter.ToUInt32(rev, 0);
                micro = "0x" + (hi != 0 ? hi : lo).ToString("X");
            }
            return (micro, key.GetValue("Identifier") as string);
        }
        catch
        {
            return (null, null);
        }
    }
}
