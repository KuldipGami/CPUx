using System.IO;

namespace CPUx.Core;

public static class Log
{
    private static readonly object Gate = new();
    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CPUx");
    public static string FilePath { get; } = Path.Combine(Directory, "cpux.log");

    static Log()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
        }
        catch { /* logging must never throw */ }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    public static void Error(string context, Exception ex) => Write($"[{context}] {ex.GetType().Name}: {ex.Message}");
}
