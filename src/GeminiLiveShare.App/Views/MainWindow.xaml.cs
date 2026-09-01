using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.App.Tray;
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
    public SettingsViewModel SettingsViewModel { get; }
    private GlobalHotkey? _overlayHotkey;
    private HwndSource? _windowSource;
    private OverlayWindow? _overlayWindow;
    private bool _isSidebarCollapsed;
    private GlobalHotkeyConfiguration _registeredHotkey = GlobalHotkeyConfiguration.Default;
    private GlobalHotkeyConfiguration _settingsHotkeyConfiguration = GlobalHotkeyConfiguration.Default;
    private bool _isCapturingHotkey;
    private bool _sidebarWasCollapsedBeforeSettings;
    private readonly TrayIconManager _trayIconManager;
    private bool _isExiting;

    public MainWindow(
        MainViewModel viewModel,
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings,
        SessionOrchestrator sessionOrchestrator,
        OverlayAppearanceSettings overlaySettings)
    {
        _viewModel = viewModel;
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _sessionOrchestrator = sessionOrchestrator;
        _overlaySettings = overlaySettings;
        SettingsViewModel = new SettingsViewModel(_apiKeyVault, _filterSettings, _overlaySettings);
        _settingsHotkeyConfiguration = new GlobalHotkeySettings().Load();
        InitializeComponent();
        DataContext = viewModel;
        UpdateThemeButtons();
        UpdateHotkeyDisplay();
        _trayIconManager = new TrayIconManager(
            RestoreFromTray,
            ToggleOverlayFromTray,
            ExitApplication);
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
        _sidebarWasCollapsedBeforeSettings = _isSidebarCollapsed;
        _isSidebarCollapsed = false;
        SidebarColumn.Width = new GridLength(360);
        HistoryPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        SidebarToggleButton.ToolTip = "Collapse settings";
    }

    private void OnBackFromSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Visible;
        _isSidebarCollapsed = _sidebarWasCollapsedBeforeSettings;
        SidebarColumn.Width = _isSidebarCollapsed ? new GridLength(0) : new GridLength(280);
        SidebarToggleButton.ToolTip = _isSidebarCollapsed ? "Show history" : "Collapse history";
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is null)
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

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    public void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            if (!IsVisible)
            {
                Show();
            }

            WindowState = WindowState.Normal;
            Activate();
            Focus();
        });
    }

    public void ToggleOverlayFromTray()
    {
        Dispatcher.BeginInvoke(new Action(HandleHotkey));
    }

    public void ExitApplication()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isExiting)
            {
                return;
            }

            _isExiting = true;
            _trayIconManager.Dispose();
            System.Windows.Application.Current.Shutdown();
        }));
    }

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

    private void OnUpdateKeyClick(object sender, RoutedEventArgs e)
    {
        ApiKeyEditor.Visibility = ApiKeyEditor.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (ApiKeyEditor.Visibility == Visibility.Visible)
        {
            ApiKeyInput.Clear();
            ApiKeyInput.Focus();
        }
    }

    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        if (SettingsViewModel.Save(ApiKeyInput.Password))
        {
            ApiKeyInput.Clear();
            ApiKeyEditor.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelKeyClick(object sender, RoutedEventArgs e)
    {
        ApiKeyInput.Clear();
        ApiKeyEditor.Visibility = Visibility.Collapsed;
    }

    private void OnDeleteKeyClick(object sender, RoutedEventArgs e) => DeleteConfirmationPopup.IsOpen = true;

    private void OnCancelDeleteClick(object sender, RoutedEventArgs e) => DeleteConfirmationPopup.IsOpen = false;

    private void OnConfirmDeleteClick(object sender, RoutedEventArgs e)
    {
        DeleteConfirmationPopup.IsOpen = false;
        SettingsViewModel.DeleteApiKey();
    }

    private void OnLightThemeClick(object sender, RoutedEventArgs e)
    {
        SettingsViewModel.IsOverlayDark = false;
        _overlayWindow?.ApplyCurrentTheme();
        UpdateThemeButtons();
    }

    private void OnDarkThemeClick(object sender, RoutedEventArgs e)
    {
        SettingsViewModel.IsOverlayDark = true;
        _overlayWindow?.ApplyCurrentTheme();
        UpdateThemeButtons();
    }

    private void UpdateThemeButtons()
    {
        LightThemeButton.IsChecked = !SettingsViewModel.IsOverlayDark;
        DarkThemeButton.IsChecked = SettingsViewModel.IsOverlayDark;
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        SettingsViewModel.ResetOverlayPosition();
        SettingsViewModel.StatusMessage = "Overlay position reset to top center.";
    }

    private void OnChangeHotkeyClick(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        ChangeHotkeyButton.Content = "Press a key...";
        HotkeyError.Text = string.Empty;
        HotkeyError.Visibility = Visibility.Collapsed;
        Keyboard.Focus(ChangeHotkeyButton);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        HotkeyModifiers modifiers = GetHotkeyModifiers(Keyboard.Modifiers);
        if ((modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift)) == HotkeyModifiers.None)
        {
            ShowHotkeyError("Please include Ctrl, Alt, or Shift");
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            ShowHotkeyError("That key combination is not available");
            return;
        }

        GlobalHotkeyConfiguration configuration = new(modifiers, (uint)virtualKey);
        if (!TryUpdateGlobalHotkey(configuration))
        {
            ShowHotkeyError("Unable to register that key combination");
            return;
        }

        try
        {
            new GlobalHotkeySettings().Save(configuration);
            _settingsHotkeyConfiguration = configuration;
            SettingsViewModel.StatusMessage = "Global hotkey updated.";
            CancelHotkeyCapture();
        }
        catch (Exception ex)
        {
            ShowHotkeyError($"Could not save the hotkey: {ex.Message}");
        }
    }

    private void CancelHotkeyCapture()
    {
        _isCapturingHotkey = false;
        ChangeHotkeyButton.Content = "Change";
        HotkeyError.Visibility = Visibility.Collapsed;
        UpdateHotkeyDisplay();
    }

    private void ShowHotkeyError(string message)
    {
        HotkeyError.Text = message;
        HotkeyError.Visibility = Visibility.Visible;
    }

    private void UpdateHotkeyDisplay()
    {
        List<string> parts = [];
        if ((_settingsHotkeyConfiguration.Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((_settingsHotkeyConfiguration.Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((_settingsHotkeyConfiguration.Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((_settingsHotkeyConfiguration.Modifiers & HotkeyModifiers.Windows) != 0) parts.Add("Win");
        parts.Add(GetKeyDisplayName(KeyInterop.KeyFromVirtualKey((int)_settingsHotkeyConfiguration.VirtualKey)));
        HotkeyDisplay.Text = string.Join(" + ", parts);
    }

    private static string GetKeyDisplayName(Key key) => key is >= Key.D0 and <= Key.D9
        ? ((int)key - (int)Key.D0).ToString()
        : key.ToString();

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static HotkeyModifiers GetHotkeyModifiers(ModifierKeys modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) result |= HotkeyModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= HotkeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= HotkeyModifiers.Shift;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= HotkeyModifiers.Windows;
        return result;
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
        _trayIconManager.Dispose();
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
