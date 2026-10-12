using CPUx.Core;
using Forms = System.Windows.Forms;

namespace CPUx.Services;

/// <summary>Notification-area icon with a live tooltip, quick menu and temperature alerts.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private DateTime _lastAlert = DateTime.MinValue;

    public TrayIcon(Action show, Action mini, Action exit)
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "CPUx",
            Visible = true,
        };
        try { _icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch { _icon.Icon = System.Drawing.SystemIcons.Application; }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open CPUx", null, (_, _) => show());
        menu.Items.Add("Mini monitor", null, (_, _) => mini());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }

    public void Update(Snapshot s)
    {
        var gpu = s.Gpus.FirstOrDefault();
        string text = $"CPUx\nCPU {s.Cpu.Usage:0}%"
                      + (double.IsNaN(s.Cpu.PackageTemp) ? "" : $" · {Fmt.Temp(s.Cpu.PackageTemp)}")
                      + (gpu == null ? "" : $"\nGPU {gpu.Usage:0}%" + (double.IsNaN(gpu.Temp) ? "" : $" · {Fmt.Temp(gpu.Temp)}"))
                      + $"\nRAM {s.Memory.Load:0}%"
                      + (s.Battery is { Percent: >= 0 } b ? $"\nBattery {b.Percent}%" : "");
        _icon.Text = text.Length > 127 ? text[..127] : text;

        var settings = AppSettings.Current;
        if (!settings.TempAlerts || (DateTime.Now - _lastAlert).TotalMinutes < 5) return;
        double limit = settings.AlertThreshold;
        string? hot = null;
        if (s.Cpu.PackageTemp >= limit) hot = $"CPU is at {Fmt.Temp(s.Cpu.PackageTemp)}";
        else if (s.Gpus.FirstOrDefault(g => g.Temp >= limit) is { } g) hot = $"GPU is at {Fmt.Temp(g.Temp)}";
        if (hot == null) return;
        _lastAlert = DateTime.Now;
        _icon.ShowBalloonTip(5000, "High temperature", $"{hot} (alert threshold {Fmt.Temp(limit)}).", Forms.ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
