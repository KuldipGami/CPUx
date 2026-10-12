namespace CPUx.Core;

/// <summary>Human-friendly formatting for hardware values.</summary>
public static class Fmt
{
    public const string Dash = "—";
    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(double bytes, int decimals = 1)
    {
        if (double.IsNaN(bytes)) return Dash;
        if (bytes < 0) bytes = 0;
        int i = 0;
        while (bytes >= 1024 && i < ByteUnits.Length - 1) { bytes /= 1024; i++; }
        if (i == 0) return $"{bytes:0} B";
        string fmt = bytes >= 100 ? "0" : "F" + decimals;
        return bytes.ToString(fmt) + " " + ByteUnits[i];
    }

    /// <summary>Decimal (1000-based) size, as printed on drive labels.</summary>
    public static string BytesDecimal(double bytes)
    {
        string[] u = { "B", "kB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (bytes >= 1000 && i < u.Length - 1) { bytes /= 1000; i++; }
        return (bytes >= 100 ? bytes.ToString("0") : bytes.ToString("0.#")) + " " + u[i];
    }

    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";

    public static string Bits(double bytesPerSec)
    {
        double b = Math.Max(0, bytesPerSec * 8);
        if (b >= 1e9) return $"{b / 1e9:0.##} Gbps";
        if (b >= 1e6) return $"{b / 1e6:0.#} Mbps";
        if (b >= 1e3) return $"{b / 1e3:0} Kbps";
        return $"{b:0} bps";
    }

    public static string LinkSpeed(long bitsPerSec)
    {
        if (bitsPerSec <= 0) return Dash;
        if (bitsPerSec >= 1_000_000_000) return $"{bitsPerSec / 1e9:0.#} Gbps";
        if (bitsPerSec >= 1_000_000) return $"{bitsPerSec / 1e6:0} Mbps";
        return $"{bitsPerSec / 1e3:0} Kbps";
    }

    public static string Pct(double v, int decimals = 0) =>
        double.IsNaN(v) ? Dash : v.ToString("F" + decimals) + "%";

    public static string MHz(double mhz)
    {
        if (double.IsNaN(mhz) || mhz <= 0) return Dash;
        return mhz >= 1000 ? $"{mhz / 1000:F2} GHz" : $"{mhz:F0} MHz";
    }

    public static string Temp(double celsius)
    {
        if (double.IsNaN(celsius)) return Dash;
        return AppSettings.Current.Fahrenheit ? $"{celsius * 9 / 5 + 32:F0} °F" : $"{celsius:F0} °C";
    }

    public static string Watts(double w)
    {
        if (double.IsNaN(w)) return Dash;
        return w < 10 ? $"{w:F1} W" : $"{w:F0} W";
    }

    public static string Volts(double v) => double.IsNaN(v) ? Dash : $"{v:F3} V";

    public static string Rpm(double rpm) => double.IsNaN(rpm) ? Dash : $"{rpm:F0} RPM";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string ShortDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min";
        return $"{t.Seconds} s";
    }

    public static string Num(long n) => n.ToString("N0");

    public static string Date(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : Dash;
    public static string Stamp(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : Dash;

    public static string YesNo(bool? b) => b switch { true => "Yes", false => "No", _ => Dash };
    public static string OnOff(bool? b) => b switch { true => "Enabled", false => "Disabled", _ => Dash };

    public static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? Dash : s.Trim();
}
