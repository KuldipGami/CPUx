using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CPUx.Core;

/// <summary>User preferences persisted to %AppData%\CPUx\settings.json.</summary>
public sealed partial class AppSettings : ObservableObject
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CPUx", "settings.json");

    public static AppSettings Current { get; private set; } = new();

    [ObservableProperty] private string _theme = "Dark";
    [ObservableProperty] private string _accent = "#4C8DFF";
    [ObservableProperty] private int _intervalMs = 1000;
    [ObservableProperty] private int _historySeconds = 60;
    [ObservableProperty] private bool _fahrenheit;
    [ObservableProperty] private bool _enableSensors = true;
    [ObservableProperty] private bool _pollNvidia = true;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _tempAlerts = true;
    [ObservableProperty] private int _alertThreshold = 90;
    [ObservableProperty] private bool _showAllAdapters;
    [ObservableProperty] private bool _hardwareRendering;
    [ObservableProperty] private bool _fanSafety = true;

    public double WindowWidth { get; set; } = 1320;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public int HistoryPoints => Math.Max(10, (int)Math.Round(HistorySeconds * 1000.0 / Math.Max(250, IntervalMs)));

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded != null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings.Load", ex);
        }
        Current.IntervalMs = Math.Clamp(Current.IntervalMs, 250, 10000);
        Current.HistorySeconds = Math.Clamp(Current.HistorySeconds, 30, 600);
        Current.AlertThreshold = Math.Clamp(Current.AlertThreshold, 50, 110);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Settings.Save", ex);
        }
    }
}
