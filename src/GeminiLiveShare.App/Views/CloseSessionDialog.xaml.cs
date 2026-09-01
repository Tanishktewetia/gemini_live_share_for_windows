using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GeminiLiveShare.App.Views;

public partial class CloseSessionDialog : Window
{
    public CloseSessionDialog()
    {
        InitializeComponent();
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is null)
        {
            DragMove();
        }
    }

    private void OnCloseSessionClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
