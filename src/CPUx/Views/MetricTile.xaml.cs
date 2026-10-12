using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CPUx.Core;

namespace CPUx.Views;

public partial class MetricTile : UserControl
{
    public MetricTile() => InitializeComponent();

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(MetricTile), new PropertyMetadata(def));

    public static readonly DependencyProperty TitleProperty = Reg(nameof(Title), "");
    public static readonly DependencyProperty CaptionProperty = Reg(nameof(Caption), "");
    public static readonly DependencyProperty ValueProperty = Reg(nameof(Value), "");
    public static readonly DependencyProperty SubProperty = Reg(nameof(Sub), "");
    public static readonly DependencyProperty BadgeProperty = Reg(nameof(Badge), "");
    public static readonly DependencyProperty IconProperty = Reg<Geometry?>(nameof(Icon), null);
    public static readonly DependencyProperty AccentProperty = Reg<Brush>(nameof(Accent), Brushes.DodgerBlue);
    public static readonly DependencyProperty Accent2Property = Reg<Brush>(nameof(Accent2), Brushes.Orange);
    public static readonly DependencyProperty SoftProperty = Reg<Brush>(nameof(Soft), Brushes.Transparent);
    public static readonly DependencyProperty SeriesProperty = Reg<ChartSeries?>(nameof(Series), null);
    public static readonly DependencyProperty Series2Property = Reg<ChartSeries?>(nameof(Series2), null);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 100.0);
    public static readonly DependencyProperty MinimumScaleProperty = Reg(nameof(MinimumScale), 1.0);
    public static readonly DependencyProperty ScaleUnitProperty = Reg(nameof(ScaleUnit), "");
    public static readonly DependencyProperty CommandProperty = Reg<ICommand?>(nameof(Command), null);
    public static readonly DependencyProperty CommandParameterProperty = Reg<object?>(nameof(CommandParameter), null);

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Sub { get => (string)GetValue(SubProperty); set => SetValue(SubProperty, value); }
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Accent2 { get => (Brush)GetValue(Accent2Property); set => SetValue(Accent2Property, value); }
    public Brush Soft { get => (Brush)GetValue(SoftProperty); set => SetValue(SoftProperty, value); }
    public ChartSeries? Series { get => (ChartSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public ChartSeries? Series2 { get => (ChartSeries?)GetValue(Series2Property); set => SetValue(Series2Property, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double MinimumScale { get => (double)GetValue(MinimumScaleProperty); set => SetValue(MinimumScaleProperty, value); }
    public string ScaleUnit { get => (string)GetValue(ScaleUnitProperty); set => SetValue(ScaleUnitProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
}
