using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GeminiLiveShare.Core.Gemini;

namespace GeminiLiveShare.App.Views;

public partial class OverlayWindow : Window
{
    private const double CollapsedSize = 72;
    private const double ExpandedWidth = 296;

    private readonly SessionOrchestrator? _sessionOrchestrator;
    private bool _isExpanded = true;

    public OverlayWindow(SessionOrchestrator? sessionOrchestrator = null)
    {
        InitializeComponent();
        _sessionOrchestrator = sessionOrchestrator;
        if (_sessionOrchestrator is not null)
        {
            _sessionOrchestrator.SessionStateChanged += OnSessionStateChanged;
            _sessionOrchestrator.ScreenShareStateChanged += OnMediaStateChanged;
            _sessionOrchestrator.MicrophoneStateChanged += OnMediaStateChanged;
            _sessionOrchestrator.SpeakingStateChanged += OnSpeakingStateChanged;
        }

        Closed += OnClosed;
        UpdateMediaState();
        UpdateSpeakingState();
        UpdateVisualState();
    }

    private async void OnScreenShareClick(object sender, RoutedEventArgs e)
    {
        if (_sessionOrchestrator is null || !_sessionOrchestrator.IsRunning)
        {
            UpdateMediaState();
            return;
        }

        ScreenShareButton.IsEnabled = false;
        try
        {
            await _sessionOrchestrator.SetScreenShareEnabledAsync(!_sessionOrchestrator.IsScreenShareOn);
        }
        catch (Exception)
        {
            // The orchestrator reports capture failures through StatusChanged; keep the overlay usable.
        }
        finally
        {
            UpdateMediaState();
        }
    }

    private async void OnMicrophoneClick(object sender, RoutedEventArgs e)
    {
        if (_sessionOrchestrator is null || !_sessionOrchestrator.IsRunning)
        {
            UpdateMediaState();
            return;
        }

        MicrophoneButton.IsEnabled = false;
        try
        {
            await _sessionOrchestrator.SetMicrophoneEnabledAsync(!_sessionOrchestrator.IsMicrophoneOn);
        }
        catch (Exception)
        {
            // The orchestrator reports capture failures through StatusChanged; keep the overlay usable.
        }
        finally
        {
            UpdateMediaState();
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => DispatchMediaStateUpdate();

    private void OnMediaStateChanged(object? sender, EventArgs e) => DispatchMediaStateUpdate();

    private void OnSpeakingStateChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(UpdateSpeakingState);
    }

    private void DispatchMediaStateUpdate()
    {
        _ = Dispatcher.InvokeAsync(UpdateMediaState);
    }

    private void UpdateMediaState()
    {
        bool hasActiveSession = _sessionOrchestrator?.IsRunning == true;
        ScreenShareButton.IsEnabled = hasActiveSession;
        MicrophoneButton.IsEnabled = hasActiveSession;
        ScreenShareButton.IsChecked = hasActiveSession && _sessionOrchestrator!.IsScreenShareOn;
        MicrophoneButton.IsChecked = hasActiveSession && _sessionOrchestrator!.IsMicrophoneOn;
    }

    private void UpdateSpeakingState()
    {
        bool isSpeaking = _sessionOrchestrator?.IsSpeaking == true;
        Storyboard expandedPulse = (Storyboard)FindResource("ExpandedSpeakingPulse");
        Storyboard collapsedPulse = (Storyboard)FindResource("CollapsedSpeakingPulse");

        if (isSpeaking)
        {
            expandedPulse.Begin(this, true);
            collapsedPulse.Begin(this, true);
            return;
        }

        expandedPulse.Remove(this);
        collapsedPulse.Remove(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_sessionOrchestrator is null)
        {
            return;
        }

        _sessionOrchestrator.SessionStateChanged -= OnSessionStateChanged;
        _sessionOrchestrator.ScreenShareStateChanged -= OnMediaStateChanged;
        _sessionOrchestrator.MicrophoneStateChanged -= OnMediaStateChanged;
        _sessionOrchestrator.SpeakingStateChanged -= OnSpeakingStateChanged;
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

    public void ToggleExpandedState()
    {
        _isExpanded = !_isExpanded;
        UpdateVisualState();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseSessionDialog dialog = new()
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
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