using System.Windows;
using System.Windows.Controls;

namespace CPUx.Controls;

/// <summary>Responsive column layout: picks a column count from the available width and drops each child into the shortest column.</summary>
public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(380.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    private int Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        int cols = (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing));
        cols = Math.Max(1, cols);
        if (MaxColumns > 0) cols = Math.Min(cols, MaxColumns);
        int visible = InternalChildren.Cast<UIElement>().Count(c => c.Visibility != Visibility.Collapsed);
        if (visible > 0) cols = Math.Min(cols, visible);
        return Math.Max(1, cols);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        int cols = Columns(width);
        double colW = Math.Max(0, (width - Spacing * (cols - 1)) / cols);
        var heights = new double[cols];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(colW, double.PositiveInfinity));
            int c = Shortest(heights);
            heights[c] += child.DesiredSize.Height + Spacing;
        }
        double h = heights.Length == 0 ? 0 : Math.Max(0, heights.Max() - Spacing);
        return new Size(width, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Columns(finalSize.Width);
        double colW = Math.Max(0, (finalSize.Width - Spacing * (cols - 1)) / cols);
        var heights = new double[cols];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            int c = Shortest(heights);
            double x = c * (colW + Spacing);
            child.Arrange(new Rect(x, heights[c], colW, child.DesiredSize.Height));
            heights[c] += child.DesiredSize.Height + Spacing;
        }
        return finalSize;
    }

    private static int Shortest(double[] heights)
    {
        int best = 0;
        for (int i = 1; i < heights.Length; i++)
            if (heights[i] < heights[best] - 0.5) best = i;
        return best;
    }
}
