using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GeminiLiveShare.App.Views;

public partial class OverlayWindow : Window
{
    private const double CollapsedSize = 72;
    private const double ExpandedWidth = 296;

    private bool _isExpanded = true;

    public OverlayWindow()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    private void OnCollapsedMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DragOrToggle();
    }

    private void OnExpandedCenterMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DragOrToggle();
    }

    private void OnExpandedMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        DragWindow();
    }

    private void DragOrToggle()
    {
        Point initialPosition = new(Left, Top);
        DragWindow();

        if (AreClose(initialPosition.X, Left) && AreClose(initialPosition.Y, Top))
        {
            _isExpanded = !_isExpanded;
            UpdateVisualState();
        }
    }

    private void DragWindow()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may be released before WPF starts its drag loop.
        }
    }

    private void UpdateVisualState()
    {
        ExpandedShell.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        CollapsedShell.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
        Width = _isExpanded ? ExpandedWidth : CollapsedSize;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            this,
            "Are you sure you want to close this session?",
            "Close session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            Close();
        }
    }

    private static bool AreClose(double first, double second) => Math.Abs(first - second) < 0.5;

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