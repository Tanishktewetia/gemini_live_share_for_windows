using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Interop;

namespace GeminiLiveShare.App.Views;
public partial class MainWindow : Window
{
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly ISensitiveContentFilterSettings _filterSettings;
    private readonly SessionOrchestrator _sessionOrchestrator;
    private readonly MainViewModel _viewModel;
    private GlobalHotkey? _overlayHotkey;
    private HwndSource? _windowSource;
    private OverlayWindow? _overlayWindow;

    public MainWindow(
        MainViewModel viewModel,
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings,
        SessionOrchestrator sessionOrchestrator)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _sessionOrchestrator = sessionOrchestrator;
        viewModel.Messages.CollectionChanged += (_, _) =>
        {
            if (viewModel.Messages.Count > 0)
            {
                MessageList.ScrollIntoView(viewModel.Messages[^1]);
            }
        };
        viewModel.SettingsRequested += OnSettingsRequested;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        SettingsWindow settingsWindow = new(new SettingsViewModel(_apiKeyVault, _filterSettings))
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(windowHandle);
        _windowSource?.AddHook(WindowMessageHook);

        GlobalHotkeyConfiguration configuration = new GlobalHotkeySettings().Load();
        try
        {
            HotkeyModifiers modifiers = configuration.Modifiers | HotkeyModifiers.NoRepeat;
            _overlayHotkey = new GlobalHotkey(windowHandle, 1, modifiers, configuration.VirtualKey);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Another application may already own the configured combination; keep this app usable.
        }
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == GlobalHotkey.WindowMessage && _overlayHotkey is not null && wParam.ToInt32() == _overlayHotkey.Id)
        {
            HandleHotkey();
            handled = true;
        }

        return nint.Zero;
    }

    private async void HandleHotkey()
    {
        if (_viewModel.IsBusy)
        {
            return;
        }

        ShowOverlay();
        if (_sessionOrchestrator.IsRunning)
        {
            if (_overlayWindow?.ConfirmStopSession() != true)
            {
                return;
            }
        }

        if (_viewModel.StartOrStopCommand.CanExecute(null))
        {
            await _viewModel.StartOrStopCommand.ExecuteAsync(null);
        }
    }

    private void ShowOverlay()
    {
        if (_overlayWindow is null)
        {
            _overlayWindow = new OverlayWindow(_sessionOrchestrator);
            _overlayWindow.StopSessionRequested += OnStopSessionRequested;
            _overlayWindow.Closed += OnOverlayClosed;
        }

        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
        }
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (_overlayWindow is not null)
        {
            _overlayWindow.Closed -= OnOverlayClosed;
            _overlayWindow.StopSessionRequested -= OnStopSessionRequested;
            _overlayWindow = null;
        }
    }

    private async void OnStopSessionRequested(object? sender, EventArgs e)
    {
        if (!_viewModel.IsBusy && _sessionOrchestrator.IsRunning && _viewModel.StartOrStopCommand.CanExecute(null))
        {
            await _viewModel.StartOrStopCommand.ExecuteAsync(null);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _overlayHotkey?.Dispose();
        _overlayHotkey = null;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
    }
}