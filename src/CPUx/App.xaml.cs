using System.Windows;
using System.Windows.Threading;
using CPUx.Core;
using CPUx.Services;
using CPUx.ViewModels;
using Microsoft.Win32;

namespace CPUx;

public partial class App : Application
{
    private MonitorService? _monitor;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private MiniWindow? _mini;
    private bool _crashShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            Log.Write($"[Fatal] {a.ExceptionObject}");
            try { FanService.Instance.RestoreAll(); } catch { } // never leave fans on a fixed speed after a crash
        };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Task", a.Exception); a.SetObserved(); };
        Log.Write($"CPUx {typeof(App).Assembly.GetName().Version?.ToString(3)} starting (admin: {SystemInfoReader.IsAdmin()}, packaged: {PackagedApp.IsPackaged})");

        AppSettings.Load();
        // Software rendering keeps memory ~150 MB lower; the UI is light enough that CPU cost is about the same.
        if (!AppSettings.Current.HardwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        ThemeManager.Apply();
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (AppSettings.Current.Theme == "System") Dispatcher.BeginInvoke(ThemeManager.Apply);
        };

        _monitor = new MonitorService(Dispatcher);
        MonitorService.Instance = _monitor;
        _vm = new MainViewModel(_monitor);

        // Headless report: CPUx.exe --export "C:\path\report.html" (.html, .json or .txt)
        int exportArg = Array.FindIndex(e.Args, a => a.Equals("--export", StringComparison.OrdinalIgnoreCase));
        if (exportArg >= 0 && exportArg + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = ExportAndExit(System.IO.Path.GetFullPath(e.Args[exportArg + 1]));
            return;
        }
        _window = new MainWindow { DataContext = _vm };
        MainWindow = _window;

        _tray = new TrayIcon(ShowMain, ShowMiniMonitor, Shutdown);
        _vm.SnapshotApplied += s => _tray?.Update(s);

        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) || AppSettings.Current.StartMinimized
                         || PackagedApp.LaunchedByStartupTask();
        if (minimized && AppSettings.Current.MinimizeToTray)
        {
            // Stay in the tray until the user opens the window.
        }
        else
        {
            if (minimized) _window.WindowState = WindowState.Minimized;
            _window.Show();
        }

        // Optional: --page "Graphics" opens a specific page on launch.
        int pageArg = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (pageArg >= 0 && pageArg + 1 < e.Args.Length) _vm.NavigateCommand.Execute(e.Args[pageArg + 1]);

        _monitor.Start();
        _ = _vm.LoadInventoryAsync();

        // Optional: --mini also opens the always-on-top mini monitor.
        if (e.Args.Contains("--mini", StringComparer.OrdinalIgnoreCase)) ShowMiniMonitor();
    }

    private async Task ExportAndExit(string path)
    {
        int code = 0;
        try
        {
            _monitor!.Start();
            await _vm!.LoadInventoryAsync();
            await Task.Delay(Math.Max(2500, AppSettings.Current.IntervalMs * 3)); // let live readings settle
            await ReportExporter.WriteAsync(_vm, path);
            Log.Write($"Report exported to {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Headless export", ex);
            code = 1;
        }
        Shutdown(code);
    }

    public void ShowMain()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = AppSettings.Current.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        _window.Activate();
    }

    public void ShowMiniMonitor()
    {
        if (_vm == null) return;
        if (_mini == null || !_mini.IsVisible)
        {
            _mini = new MiniWindow { DataContext = _vm };
            _mini.Closed += (_, _) => _mini = null;
        }
        _mini.Show();
        _mini.Activate();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"[UI] {e.Exception}");
        e.Handled = true;
        if (_crashShown) return;
        _crashShown = true;
        MessageBox.Show($"Something went wrong, but CPUx will keep running.\n\n{e.Exception.Message}\n\nDetails were written to {Log.FilePath}",
            "CPUx", MessageBoxButton.OK, MessageBoxImage.Warning);
        _crashShown = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Stress.Shutdown();
        try { FanService.Instance.RestoreAll(); } catch (Exception ex) { Log.Error("Fan restore", ex); }
        _tray?.Dispose();
        _monitor?.Dispose();
        AppSettings.Current.Save();
        Log.Write("CPUx exiting");
        base.OnExit(e);
    }
}
