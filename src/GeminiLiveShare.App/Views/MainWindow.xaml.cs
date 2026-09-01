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
    private readonly OverlayAppearanceSettings _overlaySettings;
    private readonly MainViewModel _viewModel;
    private GlobalHotkey? _overlayHotkey;
    private HwndSource? _windowSource;
    private OverlayWindow? _overlayWindow;
    private bool _isSidebarCollapsed;
    private GlobalHotkeyConfiguration _registeredHotkey = GlobalHotkeyConfiguration.Default;

    public MainWindow(
        MainViewModel viewModel,
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings,
        SessionOrchestrator sessionOrchestrator,
        OverlayAppearanceSettings overlaySettings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _sessionOrchestrator = sessionOrchestrator;
        _overlaySettings = overlaySettings;
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
        SettingsWindow settingsWindow = new(
            new SettingsViewModel(_apiKeyVault, _filterSettings, _overlaySettings),
            TryUpdateGlobalHotkey)
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

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        _isSidebarCollapsed = !_isSidebarCollapsed;
        SidebarColumn.Width = _isSidebarCollapsed ? new GridLength(0) : new GridLength(260);
        SidebarToggleButton.ToolTip = _isSidebarCollapsed ? "Show history" : "Collapse history";
    }

    private async void OnStartNewConversationClick(object sender, RoutedEventArgs e)
    {
        ShowOverlay();
        if (_viewModel.NewConversationCommand.CanExecute(null))
        {
            await _viewModel.NewConversationCommand.ExecuteAsync(null);
        }
    }

    private async void OnRenameSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ChatSessionViewModel session })
        {
            return;
        }

        RenameSessionDialog dialog = new(session.Summary)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.RenameSessionAsync(session, dialog.EnteredTitle);
        }
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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(windowHandle);
        _windowSource?.AddHook(WindowMessageHook);

        GlobalHotkeyConfiguration configuration = new GlobalHotkeySettings().Load();
        _registeredHotkey = configuration;
        try
        {
            _overlayHotkey = CreateGlobalHotkey(configuration);
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
            _overlayWindow = new OverlayWindow(_sessionOrchestrator, _overlaySettings);
            _overlayWindow.StartSessionRequested += OnStartSessionRequested;
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
            _overlayWindow.StartSessionRequested -= OnStartSessionRequested;
            _overlayWindow.StopSessionRequested -= OnStopSessionRequested;
            _overlayWindow = null;
        }
    }

    private async void OnStartSessionRequested(object? sender, EventArgs e)
    {
        if (!_viewModel.IsBusy && !_sessionOrchestrator.IsRunning && _viewModel.StartOrStopCommand.CanExecute(null))
        {
            await _viewModel.StartOrStopCommand.ExecuteAsync(null);
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

    private GlobalHotkey CreateGlobalHotkey(GlobalHotkeyConfiguration configuration) =>
        new(new WindowInteropHelper(this).Handle, 1,
            configuration.Modifiers | HotkeyModifiers.NoRepeat, configuration.VirtualKey);

    private bool TryUpdateGlobalHotkey(GlobalHotkeyConfiguration configuration)
    {
        GlobalHotkeyConfiguration previousConfiguration = _registeredHotkey;
        _overlayHotkey?.Dispose();
        _overlayHotkey = null;

        try
        {
            _overlayHotkey = CreateGlobalHotkey(configuration);
            _registeredHotkey = configuration;
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            try
            {
                _overlayHotkey = CreateGlobalHotkey(previousConfiguration);
                _registeredHotkey = previousConfiguration;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _overlayHotkey = null;
            }

            return false;
        }
    }
}
