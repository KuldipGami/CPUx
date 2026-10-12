using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using CPUx.Core;
using CPUx.ViewModels;

namespace CPUx;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        SourceInitialized += (_, _) => ApplyTitleBar();
        ThemeManager.ThemeChanged += ApplyTitleBar;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel o) o.PropertyChanged -= OnVmChanged;
            if (e.NewValue is MainViewModel n) { n.PropertyChanged += OnVmChanged; UpdateIndicators(n); }
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPaused) && sender is MainViewModel vm) UpdateIndicators(vm);
    }

    private void UpdateIndicators(MainViewModel vm)
    {
        PauseIcon.Data = (Geometry)FindResource(vm.IsPaused ? "I.Play" : "I.Pause");
        PauseText.Text = vm.IsPaused ? "Resume" : "Pause";
        LiveDot.SetResourceReference(Shape.FillProperty, vm.IsPaused ? "Warn" : "Good");
        AdminIcon.Data = (Geometry)FindResource(vm.IsAdmin ? "I.ShieldCheck" : "I.Shield");
        AdminIcon.SetResourceReference(Controls.Icon.ForegroundProperty, vm.IsAdmin ? "Good" : "Text.Secondary");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var menu = ExportButton.ContextMenu;
        menu.DataContext = DataContext;
        menu.PlacementTarget = ExportButton;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 4;
        menu.IsOpen = true;
    }

    /// <summary>Colors the native title bar to blend with the app (Windows 11).</summary>
    private void ApplyTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var c = ThemeManager.ColorResource("Color.Base", ThemeManager.IsDark ? Color.FromRgb(13, 16, 23) : Color.FromRgb(238, 241, 246));
        int caption = c.R | (c.G << 8) | (c.B << 16);
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        var t = ThemeManager.ColorResource("Color.Text", ThemeManager.IsDark ? Color.FromRgb(236, 239, 244) : Color.FromRgb(17, 24, 39));
        int text = t.R | (t.G << 8) | (t.B << 16);
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_TEXT_COLOR, ref text, sizeof(int));
        int round = 2;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && AppSettings.Current.MinimizeToTray) Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var s = AppSettings.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ActualWidth;
            s.WindowHeight = ActualHeight;
        }
        s.Save();
        ThemeManager.ThemeChanged -= ApplyTitleBar;
    }
}
