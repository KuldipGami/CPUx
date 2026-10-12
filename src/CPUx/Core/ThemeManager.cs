using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CPUx.Core;

public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;
    public static event Action? ThemeChanged;

    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Blue", "#4C8DFF"),
        ("Violet", "#8B5CF6"),
        ("Teal", "#14B8A6"),
        ("Green", "#22C55E"),
        ("Orange", "#F97316"),
        ("Rose", "#F43F5E"),
    };

    public static void Apply()
    {
        var s = AppSettings.Current;
        bool dark = s.Theme switch
        {
            "Light" => false,
            "Dark" => true,
            _ => SystemUsesDarkTheme(),
        };

        var app = Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/CPUx;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        if (dicts.Count > 0) dicts[0] = palette;
        else dicts.Add(palette);

        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(s.Accent); }
        catch { accent = Color.FromRgb(0x4C, 0x8D, 0xFF); }

        app.Resources["Color.Accent"] = accent;
        app.Resources["Accent"] = Frozen(new SolidColorBrush(accent));
        app.Resources["Accent.Soft"] = Frozen(new SolidColorBrush(Color.FromArgb(dark ? (byte)0x33 : (byte)0x22, accent.R, accent.G, accent.B)));
        app.Resources["Accent.Fg"] = Frozen(new SolidColorBrush(Luma(accent) > 0.72 ? Color.FromRgb(0x10, 0x14, 0x1C) : Colors.White));

        IsDark = dark;
        ThemeChanged?.Invoke();
    }

    public static Color ColorResource(string key, Color fallback)
    {
        if (Application.Current.TryFindResource(key) is Color c) return c;
        if (Application.Current.TryFindResource(key) is SolidColorBrush b) return b.Color;
        return fallback;
    }

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch
        {
            return true;
        }
    }
}
