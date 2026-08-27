using System.Windows;
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
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _sessionOrchestrator = sessionOrchestrator;
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

    // TEMP: manual test button for Phase 5a, remove after verification.
    private void OnTestOverlayClick(object sender, RoutedEventArgs e) => ShowOverlay();

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
            ToggleOverlay();
            handled = true;
        }

        return nint.Zero;
    }

    private void ToggleOverlay()
    {
        if (_overlayWindow is null || !_overlayWindow.IsVisible)
        {
            ShowOverlay();
            return;
        }

        _overlayWindow.ToggleExpandedState();
    }

    private void ShowOverlay()
    {
        if (_overlayWindow is null)
        {
            _overlayWindow = new OverlayWindow(_sessionOrchestrator);
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
            _overlayWindow = null;
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