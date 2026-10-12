using System.Windows;
using System.Windows.Input;

namespace CPUx;

public partial class MiniWindow : Window
{
    public MiniWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 12;
            Top = area.Top + 12;
        };
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOpenMain(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowMain();
}
