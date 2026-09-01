using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Interop;

namespace GeminiLiveShare.App.Views;

public partial class OverlayWindow : Window
{
    private const double CollapsedSize = 72;
    private const double ExpandedWidth = 324;

    private readonly SessionOrchestrator? _sessionOrchestrator;
    private readonly OverlayAppearanceSettings _appearanceSettings;
    private bool _isExpanded = true;
    private readonly DispatcherTimer _durationTimer;
    private DateTimeOffset? _sessionStartedAt;

    public event EventHandler? StopSessionRequested;

    public event EventHandler? StartSessionRequested;

    public OverlayWindow(
        SessionOrchestrator? sessionOrchestrator = null,
        OverlayAppearanceSettings? appearanceSettings = null)
    {
        InitializeComponent();
        _appearanceSettings = appearanceSettings ?? new OverlayAppearanceSettings();
        IsDarkTheme = _appearanceSettings.Theme == OverlayTheme.Dark;
        ApplyTheme();
        _durationTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateDuration(), Dispatcher);
        _sessionOrchestrator = sessionOrchestrator;
        if (_sessionOrchestrator is not null)
        {
            _sessionOrchestrator.SessionStateChanged += OnSessionStateChanged;
            _sessionOrchestrator.ScreenShareStateChanged += OnMediaStateChanged;
            _sessionOrchestrator.MicrophoneStateChanged += OnMediaStateChanged;
            _sessionOrchestrator.SpeakingStateChanged += OnSpeakingStateChanged;
            _sessionOrchestrator.ConnectionStateChanged += OnConnectionStateChanged;
        }

        Closed += OnClosed;
        Loaded += OnLoaded;
        UpdateMediaState();
        UpdateSpeakingState();
        UpdateConnectionState();
        UpdateDurationState();
        UpdateVisualState();
    }

    public bool IsDarkTheme { get; private set; }

    public void ApplyCurrentTheme()
    {
        IsDarkTheme = _appearanceSettings.Theme == OverlayTheme.Dark;
        ApplyTheme();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Rect workArea = SystemParameters.WorkArea;
        double left = (workArea.Left + workArea.Right - Width) / 2;
        double top = workArea.Top + 24;
        if (_appearanceSettings.Position == OverlayPosition.BottomCenter)
        {
            top = workArea.Bottom - Height - 24;
        }
        else if (_appearanceSettings.Position == OverlayPosition.Custom &&
                 _appearanceSettings.CustomLeft is double customLeft &&
                 _appearanceSettings.CustomTop is double customTop)
        {
            left = customLeft;
            top = customTop;
        }

        Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private void ApplyTheme()
    {
        Resources["OverlaySurfaceBrush"] = new SolidColorBrush(
            IsDarkTheme ? System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A) : System.Windows.Media.Color.FromRgb(0xF4, 0xF4, 0xF6));
        Resources["OverlaySurfaceHoverBrush"] = new SolidColorBrush(
            IsDarkTheme ? System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x12) : System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xEC));
        Resources["OverlayBorderBrush"] = new SolidColorBrush(
            IsDarkTheme ? System.Windows.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : System.Windows.Media.Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        Resources["OverlayControlBrush"] = new SolidColorBrush(
            IsDarkTheme ? System.Windows.Media.Color.FromArgb(0x38, 0x10, 0x10, 0x12) : System.Windows.Media.Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        Resources["OverlayControlHoverBrush"] = new SolidColorBrush(
            IsDarkTheme ? System.Windows.Media.Color.FromArgb(0x66, 0x58, 0x5B, 0x65) : System.Windows.Media.Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        Resources["OverlayMutedTextBrush"] = new SolidColorBrush(
            IsDarkTheme ? System.Windows.Media.Color.FromRgb(0xB8, 0xBB, 0xC4) : System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x50));
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

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            UpdateMediaState();
            UpdateConnectionState();
            UpdateDurationState();
        });
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            UpdateMediaState();
            UpdateConnectionState();
            UpdateSpeakingState();
        });
    }

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
        bool hasActiveSession = _sessionOrchestrator?.IsRunning == true && _sessionOrchestrator.IsConnected;
        ScreenShareButton.IsEnabled = hasActiveSession;
        MicrophoneButton.IsEnabled = hasActiveSession;
        ScreenShareButton.IsChecked = hasActiveSession && _sessionOrchestrator!.IsScreenShareOn;
        MicrophoneButton.IsChecked = hasActiveSession && _sessionOrchestrator!.IsMicrophoneOn;
    }

    private void UpdateSpeakingState()
    {
        bool isSpeaking = _sessionOrchestrator?.IsSpeaking == true && _sessionOrchestrator.IsConnected;
        Storyboard expandedPulse = (Storyboard)FindResource("ExpandedSpeakingPulse");
        Storyboard collapsedPulse = (Storyboard)FindResource("CollapsedSpeakingPulse");
        Storyboard expandedDots = (Storyboard)FindResource("ExpandedSpeakingDots");
        Storyboard collapsedDots = (Storyboard)FindResource("CollapsedSpeakingDots");

        if (isSpeaking)
        {
            expandedPulse.Begin(this, true);
            collapsedPulse.Begin(this, true);
            expandedDots.Begin(this, true);
            collapsedDots.Begin(this, true);
            return;
        }

        expandedPulse.Remove(this);
        collapsedPulse.Remove(this);
        expandedDots.Remove(this);
        collapsedDots.Remove(this);
    }

    private void UpdateConnectionState()
    {
        bool isBuffering = _sessionOrchestrator?.IsConnecting == true ||
            (_sessionOrchestrator?.IsRunning == true && !_sessionOrchestrator.IsConnected);
        ExpandedDots.Visibility = isBuffering ? Visibility.Collapsed : Visibility.Visible;
        CollapsedDots.Visibility = isBuffering ? Visibility.Collapsed : Visibility.Visible;
        CollapsedReadyIcon.Visibility = isBuffering ? Visibility.Collapsed : Visibility.Visible;
        ExpandedBufferingIcon.Visibility = isBuffering ? Visibility.Visible : Visibility.Collapsed;
        CollapsedBufferingIcon.Visibility = isBuffering ? Visibility.Visible : Visibility.Collapsed;
        Storyboard buffering = (Storyboard)FindResource("BufferingRotation");
        if (isBuffering)
        {
            buffering.Begin(this, true);
            CollapsedBufferingRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }
        else
        {
            buffering.Remove(this);
            CollapsedBufferingRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void UpdateDurationState()
    {
        if (_sessionOrchestrator?.IsRunning == true)
        {
            _sessionStartedAt ??= DateTimeOffset.UtcNow;
            _durationTimer.Start();
        }
        else
        {
            _durationTimer.Stop();
            _sessionStartedAt = null;
        }

        UpdateDuration();
    }

    private void UpdateDuration()
    {
        TimeSpan elapsed = _sessionStartedAt is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - _sessionStartedAt.Value;
        SessionDurationText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
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
        _sessionOrchestrator.ConnectionStateChanged -= OnConnectionStateChanged;
        _durationTimer.Stop();
    }

    private void OnCollapsedMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_sessionOrchestrator?.IsRunning != true)
        {
            StartSessionRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        DragOrToggle();
    }

    private void OnExpandedCenterMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_sessionOrchestrator?.IsRunning != true)
        {
            StartSessionRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        DragOrToggle();
    }

    private void OnExpandedMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        DragWindow();
    }

    private void DragOrToggle()
    {
        System.Windows.Point initialPosition = new(Left, Top);
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
            _appearanceSettings.SaveCurrentPosition(Left, Top);
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

    public void Expand()
    {
        if (_isExpanded)
        {
            return;
        }

        _isExpanded = true;
        UpdateVisualState();
    }

    public bool ConfirmStopSession()
    {
        CloseSessionDialog dialog = new()
        {
            Owner = this
        };

        return dialog.ShowDialog() == true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmStopSession())
        {
            if (_sessionOrchestrator?.IsRunning == true)
            {
                StopSessionRequested?.Invoke(this, EventArgs.Empty);
            }
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
