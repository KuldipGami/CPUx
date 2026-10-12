using System.Management;

namespace CPUx.Core;

/// <summary>Small, exception-safe WMI query helper.</summary>
public static class Wmi
{
    public static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2", int timeoutSeconds = 15)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            var options = new EnumerationOptions
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                ReturnImmediately = true,
                Rewindable = false,
            };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            foreach (var o in searcher.Get()) list.Add(o);
        }
        catch (Exception ex)
        {
            Log.Write($"WMI [{scope}] {wql} -> {ex.GetType().Name}: {ex.Message}");
        }
        return list;
    }

    public static ManagementBaseObject? First(string wql, string scope = @"root\cimv2") => Query(wql, scope).FirstOrDefault();

    public static object? V(this ManagementBaseObject o, string prop)
    {
        try { return o[prop]; }
        catch { return null; }
    }

    public static string S(this ManagementBaseObject o, string prop)
    {
        var v = o.V(prop);
        return v switch
        {
            null => "",
            string s => s.Trim(),
            string[] arr => string.Join(", ", arr.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())),
            _ => v.ToString()?.Trim() ?? "",
        };
    }

    public static long L(this ManagementBaseObject o, string prop)
    {
        var v = o.V(prop);
        try { return v == null ? 0 : Convert.ToInt64(v); }
        catch { return 0; }
    }

    public static ulong U(this ManagementBaseObject o, string prop)
    {
        var v = o.V(prop);
        try { return v == null ? 0 : Convert.ToUInt64(v); }
        catch { return 0; }
    }

    public static bool? B(this ManagementBaseObject o, string prop)
    {
        var v = o.V(prop);
        return v is bool b ? b : null;
    }

    public static DateTime? D(this ManagementBaseObject o, string prop)
    {
        var s = o.V(prop) as string;
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
