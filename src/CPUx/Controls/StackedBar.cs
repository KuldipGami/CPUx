using System.Windows;
using System.Windows.Media;

namespace CPUx.Controls;

/// <summary>Horizontal bar split into up to four proportional segments (e.g. memory composition).</summary>
public sealed class StackedBar : FrameworkElement
{
    public static readonly DependencyProperty Value1Property = Reg(nameof(Value1), 0.0);
    public static readonly DependencyProperty Value2Property = Reg(nameof(Value2), 0.0);
    public static readonly DependencyProperty Value3Property = Reg(nameof(Value3), 0.0);
    public static readonly DependencyProperty Value4Property = Reg(nameof(Value4), 0.0);
    public static readonly DependencyProperty Brush1Property = RegBrush(nameof(Brush1));
    public static readonly DependencyProperty Brush2Property = RegBrush(nameof(Brush2));
    public static readonly DependencyProperty Brush3Property = RegBrush(nameof(Brush3));
    public static readonly DependencyProperty Brush4Property = RegBrush(nameof(Brush4));

    private static DependencyProperty Reg(string name, double def) => DependencyProperty.Register(
        name, typeof(double), typeof(StackedBar), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty RegBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(StackedBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value1 { get => (double)GetValue(Value1Property); set => SetValue(Value1Property, value); }
    public double Value2 { get => (double)GetValue(Value2Property); set => SetValue(Value2Property, value); }
    public double Value3 { get => (double)GetValue(Value3Property); set => SetValue(Value3Property, value); }
    public double Value4 { get => (double)GetValue(Value4Property); set => SetValue(Value4Property, value); }
    public Brush Brush1 { get => (Brush)GetValue(Brush1Property); set => SetValue(Brush1Property, value); }
    public Brush Brush2 { get => (Brush)GetValue(Brush2Property); set => SetValue(Brush2Property, value); }
    public Brush Brush3 { get => (Brush)GetValue(Brush3Property); set => SetValue(Brush3Property, value); }
    public Brush Brush4 { get => (Brush)GetValue(Brush4Property); set => SetValue(Brush4Property, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 14);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double[] values = { Value1, Value2, Value3, Value4 };
        Brush[] brushes = { Brush1, Brush2, Brush3, Brush4 };
        double total = values.Where(v => v > 0).Sum();
        double radius = Math.Min(5, h / 2);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));
        if (total <= 0)
        {
            dc.DrawRectangle(Brush4, null, new Rect(0, 0, w, h));
            dc.Pop();
            return;
        }
        const double gap = 2;
        double x = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] <= 0) continue;
            double segW = w * values[i] / total;
            double drawW = Math.Max(0, segW - (i < values.Length - 1 ? gap : 0));
            if (drawW > 0.5) dc.DrawRectangle(brushes[i], null, new Rect(x, 0, drawW, h));
            x += segW;
        }
        dc.Pop();
    }
}
