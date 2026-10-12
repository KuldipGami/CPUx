using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CPUx.Core;

namespace CPUx.Controls;

/// <summary>Lightweight streaming area chart with up to two series.</summary>
public sealed class LiveChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(ChartSeries), typeof(LiveChart), new PropertyMetadata(null, OnSeriesChanged));

    public static readonly DependencyProperty Series2Property = DependencyProperty.Register(
        nameof(Series2), typeof(ChartSeries), typeof(LiveChart), new PropertyMetadata(null, OnSeriesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LiveChart),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender, OnStyleChanged));

    public static readonly DependencyProperty Stroke2Property = DependencyProperty.Register(
        nameof(Stroke2), typeof(Brush), typeof(LiveChart),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender, OnStyleChanged));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(LiveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStyleChanged));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(LiveChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed maximum. Zero or negative means auto-scale.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LiveChart),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumScaleProperty = DependencyProperty.Register(
        nameof(MinimumScale), typeof(double), typeof(LiveChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(LiveChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.Register(
        nameof(ShowDot), typeof(bool), typeof(LiveChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Unit used for the scale label in the corner: "", "%", "B/s", "bps", "W", "C".</summary>
    public static readonly DependencyProperty ScaleUnitProperty = DependencyProperty.Register(
        nameof(ScaleUnit), typeof(string), typeof(LiveChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LiveChart),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender, OnStyleChanged));

    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
        nameof(FillOpacity), typeof(double), typeof(LiveChart),
        new FrameworkPropertyMetadata(0.32, FrameworkPropertyMetadataOptions.AffectsRender, OnStyleChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(LiveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChartSeries? Series { get => (ChartSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public ChartSeries? Series2 { get => (ChartSeries?)GetValue(Series2Property); set => SetValue(Series2Property, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Stroke2 { get => (Brush)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double MinimumScale { get => (double)GetValue(MinimumScaleProperty); set => SetValue(MinimumScaleProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowDot { get => (bool)GetValue(ShowDotProperty); set => SetValue(ShowDotProperty, value); }
    public string ScaleUnit { get => (string)GetValue(ScaleUnitProperty); set => SetValue(ScaleUnitProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    private Pen? _pen1, _pen2, _gridPen;
    private Brush? _fill1, _fill2;
    private bool _attached;

    public LiveChart()
    {
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SnapsToDevicePixels = true;
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (LiveChart)d;
        if (chart._attached)
        {
            if (e.OldValue is ChartSeries o) o.Updated -= chart.OnSeriesUpdated;
            if (e.NewValue is ChartSeries n) n.Updated += chart.OnSeriesUpdated;
        }
        chart.InvalidateVisual();
    }

    private static void OnStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (LiveChart)d;
        chart._pen1 = chart._pen2 = chart._gridPen = null;
        chart._fill1 = chart._fill2 = null;
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        if (Series != null) Series.Updated += OnSeriesUpdated;
        if (Series2 != null) Series2.Updated += OnSeriesUpdated;
        InvalidateVisual();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        if (Series != null) Series.Updated -= OnSeriesUpdated;
        if (Series2 != null) Series2.Updated -= OnSeriesUpdated;
    }

    private void OnSeriesUpdated(object? sender, EventArgs e) => InvalidateVisual();

    private static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.DodgerBlue;

    private static Brush MakeFill(Brush stroke, double opacity)
    {
        var c = ColorOf(stroke);
        var top = Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B);
        var bottom = Color.FromArgb(4, c.R, c.G, c.B);
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        if (CornerRadius > 0)
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), CornerRadius, CornerRadius));

        // Transparent background so the whole area is hit-testable for tooltips.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        if (ShowGrid && GridBrush != null)
        {
            _gridPen ??= MakePen(GridBrush, 1);
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(_gridPen, new Point(0, y), new Point(w, y));
            }
        }

        double max = Maximum;
        if (max <= 0)
        {
            double m = Math.Max(Series?.Max() ?? 0, Series2?.Max() ?? 0);
            max = NiceCeiling(Math.Max(m * 1.1, MinimumScale));
        }

        if (Series2 != null)
        {
            _pen2 ??= MakePen(Stroke2, StrokeThickness);
            _fill2 ??= MakeFill(Stroke2, FillOpacity * 0.7);
            DrawSeries(dc, Series2, _pen2, _fill2, max, w, h);
        }
        if (Series != null)
        {
            _pen1 ??= MakePen(Stroke, StrokeThickness);
            _fill1 ??= MakeFill(Stroke, FillOpacity);
            DrawSeries(dc, Series, _pen1, _fill1, max, w, h);
        }

        if (!string.IsNullOrEmpty(ScaleUnit))
        {
            string label = FormatScale(max, ScaleUnit);
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10.5, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(w - ft.Width - 6, 4));
        }

        if (CornerRadius > 0) dc.Pop();
    }

    private void DrawSeries(DrawingContext dc, ChartSeries s, Pen pen, Brush fill, double max, double w, double h)
    {
        int n = s.Count;
        if (n < 2) return;
        double step = w / (s.Capacity - 1);
        double inset = pen.Thickness / 2 + 0.5;
        double usable = h - inset * 2;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            double x0 = w - (n - 1) * step;
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * step;
                double f = Math.Clamp(s[i] / max, 0, 1);
                double y = inset + usable * (1 - f);
                var p = new Point(x, y);
                if (i == 0)
                {
                    lc.BeginFigure(p, false, false);
                    ac.BeginFigure(new Point(x, h), true, true);
                    ac.LineTo(p, false, false);
                }
                else
                {
                    lc.LineTo(p, true, true);
                    ac.LineTo(p, false, false);
                }
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);

        if (ShowDot)
        {
            double f = Math.Clamp(s[n - 1] / max, 0, 1);
            var p = new Point(w - 1.5, inset + usable * (1 - f));
            dc.DrawEllipse(pen.Brush, null, p, 3, 3);
        }
    }

    public static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nf * exp;
    }

    private static string FormatScale(double max, string unit) => unit switch
    {
        "%" => $"{max:0}%",
        "B/s" => Fmt.Rate(max),
        "bps" => Fmt.Bits(max),
        "W" => $"{max:0} W",
        "C" => Fmt.Temp(max),
        "B" => Fmt.Bytes(max),
        _ => max.ToString("0.#") + " " + unit,
    };
}
