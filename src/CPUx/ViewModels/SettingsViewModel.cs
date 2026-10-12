using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CPUx.Core;
using CPUx.Services;
using Microsoft.Win32;

namespace CPUx.ViewModels;

public sealed partial class AccentOption : ObservableObject
{
    public AccentOption(string name, string hex)
    {
        Name = name;
        Hex = hex;
    }

    public string Name { get; }
    public string Hex { get; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public override string Title => "Settings";

    public AppSettings S => AppSettings.Current;
    public IReadOnlyList<AccentOption> Accents { get; } = ThemeManager.Accents.Select(a => new AccentOption(a.Name, a.Hex)).ToList();
    public IReadOnlyList<int> Thresholds { get; } = new[] { 80, 85, 90, 95, 100 };

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _isAdmin = SystemInfoReader.IsAdmin();
    [ObservableProperty] private bool _pawnIoInstalled = HardwareSensors.IsPawnIoInstalled();
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string DataFolder => Log.Directory;

    public SettingsViewModel()
    {
        Subtitle = "Appearance, monitoring and behavior";
        IsLoading = false;
        if (PackagedApp.IsPackaged)
        {
            _startWithWindows = PackagedApp.StartupEnabled() == true;
        }
        else
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                _startWithWindows = key?.GetValue("CPUx") != null;
            }
            catch { }
        }
        S.PropertyChanged += OnSettingChanged;
        SyncAccents();
    }

    private void SyncAccents()
    {
        foreach (var a in Accents) a.IsSelected = a.Hex.Equals(S.Accent, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void SelectAccent(AccentOption? option)
    {
        if (option == null) return;
        S.Accent = option.Hex;
        SyncAccents();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
            case nameof(AppSettings.Accent):
                ThemeManager.Apply();
                break;
            case nameof(AppSettings.IntervalMs):
            case nameof(AppSettings.HistorySeconds):
                Series.ResizeAll();
                MonitorService.Instance?.Wake();
                break;
            case nameof(AppSettings.EnableSensors):
                if (S.EnableSensors) Task.Run(() => MonitorService.Instance?.OpenSensors());
                break;
        }
        S.Save();
    }

    // Segmented option helpers -----------------------------------------------------------
    public string Theme
    {
        get => S.Theme;
        set { S.Theme = value; OnPropertyChanged(); }
    }

    public int Interval
    {
        get => S.IntervalMs;
        set { S.IntervalMs = value; OnPropertyChanged(); }
    }

    public int History
    {
        get => S.HistorySeconds;
        set { S.HistorySeconds = value; OnPropertyChanged(); }
    }

    public string Accent
    {
        get => S.Accent;
        set { S.Accent = value; OnPropertyChanged(); }
    }

    private bool _syncingStartup;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncingStartup) return;
        if (PackagedApp.IsPackaged)
        {
            // Store install: the package's startup task. Windows can refuse when the user turned it off in Settings.
            bool on = PackagedApp.SetStartup(value);
            if (on != value)
            {
                _syncingStartup = true;
                StartWithWindows = on;
                _syncingStartup = false;
                if (value) Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            }
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("CPUx", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("CPUx", false);
        }
        catch (Exception ex)
        {
            Log.Error("Startup registry", ex);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { Process.Start("explorer.exe", $"\"{Log.Directory}\""); } catch { }
    }

    [RelayCommand]
    private void OpenPawnIo()
    {
        try { Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true }); } catch { }
    }
}
