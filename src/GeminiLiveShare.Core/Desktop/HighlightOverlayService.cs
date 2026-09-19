using DrawingRectangle = System.Drawing.Rectangle;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GeminiLiveShare.Core.Desktop;

/// <summary>Shows a non-activating, click-through target marker above the real Windows control.</summary>
public sealed class HighlightOverlayService : IHighlightOverlayService
{
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20;
    private const nint WsExLayered = 0x80000;
    private const nint WsExToolwindow = 0x80;
    private const nint WsExNoactivate = 0x08000000;
    private const uint WdaExcludeFromCapture = 0x11;
    private const short MouseButtonDown = unchecked((short)0x8000);
    private const int VirtualKeyLeftButton = 0x01;

    private readonly object _gate = new();
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HighlightWindow? _window;
    private TaskCompletionSource _ready = NewCompletionSource();
    private CancellationTokenSource? _autoClearCancellation;
    private volatile bool _isVisible;
    private bool _disposed;

    public bool IsVisible => _isVisible;

    public async Task ShowAsync(
        DrawingRectangle bounds,
        string label,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || _disposed)
        {
            return;
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        Dispatcher dispatcher = _dispatcher!;
        await dispatcher.InvokeAsync(() =>
        {
            _window!.ShowHighlight(bounds, label);
            _isVisible = true;
            ScheduleAutoClear(duration);
        }).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!_isVisible)
        {
            return;
        }

        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null || _disposed)
        {
            _isVisible = false;
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            _window?.HideHighlight();
            _isVisible = false;
            CancelAutoClear();
        }).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAutoClear();
        Thread? thread;
        Dispatcher? dispatcher;
        lock (_gate)
        {
            thread = _thread;
            dispatcher = _dispatcher;
        }

        if (dispatcher is not null)
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    _window?.Close();
                    _isVisible = false;
                });
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
            catch (InvalidOperationException)
            {
                // The overlay dispatcher may already be shutting down during application exit.
            }
        }

        if (thread is not null && thread.IsAlive)
        {
            thread.Join(1000);
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        StartThreadIfNeeded();
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartThreadIfNeeded()
    {
        lock (_gate)
        {
            if (_thread is not null || _disposed)
            {
                return;
            }

            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "HighlightOverlayThread"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private void ThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _window = new HighlightWindow();
        _window.InitializeNative();
        _ready.TrySetResult();
        Dispatcher.Run();
    }

    private void ScheduleAutoClear(TimeSpan duration)
    {
        CancelAutoClear();
        TimeSpan timeout = duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : duration;
        timeout = timeout > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : timeout;
        CancellationTokenSource cancellation = new();
        lock (_gate)
        {
            _autoClearCancellation = cancellation;
        }

        CancellationToken token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            try
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if ((GetAsyncKeyState(VirtualKeyLeftButton) & MouseButtonDown) != 0)
                    {
                        await HideFromWorkerAsync(token).ConfigureAwait(false);
                        return;
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                await HideFromWorkerAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task HideFromWorkerAsync(CancellationToken cancellationToken)
    {
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null || _disposed)
        {
            _isVisible = false;
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            _window?.HideHighlight();
            _isVisible = false;
            CancelAutoClear();
        }).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CancelAutoClear()
    {
        lock (_gate)
        {
            _autoClearCancellation?.Cancel();
            _autoClearCancellation?.Dispose();
            _autoClearCancellation = null;
        }
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class HighlightWindow : Window
    {
        private readonly Border _border;
        private readonly TextBlock _label;

        public HighlightWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            IsHitTestVisible = false;
            Focusable = false;

            Grid root = new();
            _border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 176, 59)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(18, 255, 176, 59))
            };
            _label = new TextBlock
            {
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(210, 30, 30, 30)),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(6, 3, 6, 3),
                Text = string.Empty
            };
            root.Children.Add(_border);
            root.Children.Add(_label);
            Content = root;
            Visibility = Visibility.Hidden;
        }

        public void InitializeNative()
        {
            Show();
            Hide();
            if (PresentationSource.FromVisual(this) is not HwndSource source)
            {
                return;
            }

            nint hwnd = source.Handle;
            nint exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
            exStyle |= WsExTransparent | WsExLayered | WsExToolwindow | WsExNoactivate;
            SetWindowLongPtr(hwnd, GwlExStyle, exStyle);
            _ = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        }

        public void ShowHighlight(DrawingRectangle bounds, string label)
        {
            if (PresentationSource.FromVisual(this) is not HwndSource source)
            {
                return;
            }

            // UI Automation returns physical screen pixels. WPF positions a Window in DIPs,
            // so use the current monitor's device transform rather than assuming 100% scaling.
            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point topLeft = fromDevice.Transform(new Point(bounds.Left - 4, bounds.Top - 4));
            Point bottomRight = fromDevice.Transform(new Point(bounds.Right + 4, bounds.Bottom + 4));

            Left = topLeft.X;
            Top = topLeft.Y;
            Width = Math.Max(24, bottomRight.X - topLeft.X);
            Height = Math.Max(24, bottomRight.Y - topLeft.Y);
            _label.Text = string.IsNullOrWhiteSpace(label) ? "Target" : label;
            Visibility = Visibility.Visible;
            Show();
        }

        public void HideHighlight()
        {
            Visibility = Visibility.Hidden;
            Hide();
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
