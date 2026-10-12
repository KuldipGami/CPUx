using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CPUx.Controls;

/// <summary>Renders a stroke icon authored on a 24x24 grid, scaled to the element size.</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.9, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Icon()
    {
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(18, availableSize.Width), Math.Min(18, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var data = Data;
        if (data == null) return;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double scale = size / 24.0;
        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;

        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Fill, pen, data);
        dc.Pop();
        dc.Pop();
    }
}
