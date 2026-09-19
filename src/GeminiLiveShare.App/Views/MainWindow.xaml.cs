using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.App.Tray;
using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Interop;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.Diagnostics;
using GeminiLiveShare.Core.Audio;

namespace GeminiLiveShare.App.Views;
public partial class MainWindow : Window
{
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly ISensitiveContentFilterSettings _filterSettings;
    private readonly SessionOrchestrator _sessionOrchestrator;
    private readonly OverlayAppearanceSettings _overlaySettings;
    private readonly IDiagnosticsDebugSettings _diagnosticsSettings;
    private readonly BrowserAgentBridge _browserAgentBridge;
    private readonly HighlightSettings _highlightSettings;
    private readonly IAudioCaptureService _audioCapture;
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
        OverlayAppearanceSettings overlaySettings,
        IDiagnosticsDebugSettings diagnosticsSettings,
        BrowserAgentBridge browserAgentBridge,
        HighlightSettings? highlightSettings = null,
        IAudioCaptureService? audioCapture = null)
    {
        _viewModel = viewModel;
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _sessionOrchestrator = sessionOrchestrator;
        _overlaySettings = overlaySettings;
        _diagnosticsSettings = diagnosticsSettings;
        _browserAgentBridge = browserAgentBridge;
        _highlightSettings = highlightSettings ?? new HighlightSettings();
        _audioCapture = audioCapture ?? new AudioCaptureService();
        SettingsViewModel = new SettingsViewModel(_apiKeyVault, _filterSettings, _diagnosticsSettings, _overlaySettings, _highlightSettings, _audioCapture);
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
        SidebarHost.Children.Remove(SettingsPanel);
        MainLayout.Children.Add(SettingsPanel);
        Grid.SetColumn(SettingsPanel, 1);
        System.Windows.Controls.Panel.SetZIndex(SettingsPanel, 1);
        SettingsPanel.Visibility = Visibility.Visible;
        SidebarToggleButton.ToolTip = "Collapse history";
    }

    private void OnBackFromSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        MainLayout.Children.Remove(SettingsPanel);
        SidebarHost.Children.Add(SettingsPanel);
        Grid.SetColumn(SettingsPanel, 0);
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

        // Keep the app alive in the tray when the user closes the main window.
        // The process should end only through the tray Exit command.
        e.Cancel = true;
        MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        ShowInTaskbar = false;
        Hide();
        _trayIconManager.ShowMinimizedToTrayHint();
    }

    public void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            if (!IsVisible)
            {
                ShowInTaskbar = true;
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

    private void OnSidebarResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isSidebarCollapsed || SettingsPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        double width = Math.Clamp(SidebarColumn.ActualWidth + e.HorizontalChange, SidebarColumn.MinWidth, SidebarColumn.MaxWidth);
        SidebarColumn.Width = new GridLength(width);
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

    private void OnOpenFramesFolderClick(object sender, RoutedEventArgs e)
    {
        OpenPath(SettingsViewModel.SentFramesDirectory, "frames folder");
    }

    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e)
    {
        OpenPath(SettingsViewModel.LogsDirectory, "logs folder");
    }

    private void OnOpenTodayLogClick(object sender, RoutedEventArgs e)
    {
        OpenPath(SettingsViewModel.TodaySessionLogPath, "today's session log");
    }

    private void OpenPath(string path, string label)
    {
        try
        {
            string target = path;
            if (Path.HasExtension(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, string.Empty);
                }
            }
            else
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SettingsViewModel.StatusMessage = $"Could not open {label}: {ex.Message}";
        }
    }
    private void OnExportSessionClick(object sender, RoutedEventArgs e)
    {
        ChatSessionViewModel? session = _viewModel.SelectedSession;
        if (session is null)
        {
            return;
        }

        ContextMenu menu = new()
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = sender as UIElement
        };

        MenuItem saveMarkdown = new() { Header = "Save as .md file" };
        saveMarkdown.Click += async (_, _) => await ExportSessionMarkdownAsync(session).ConfigureAwait(true);

        MenuItem copyFullChat = new() { Header = "Copy full chat" };
        copyFullChat.Click += async (_, _) => await CopySessionTranscriptAsync(session).ConfigureAwait(true);

        menu.Items.Add(saveMarkdown);
        menu.Items.Add(copyFullChat);
        menu.IsOpen = true;
    }

    private async Task ExportSessionMarkdownAsync(ChatSessionViewModel session)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Export conversation",
            Filter = "Markdown (*.md)|*.md",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = BuildExportFileName(session.Summary)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            IReadOnlyList<GeminiLiveShare.Core.Storage.ChatMessage> messages =
                await _viewModel.GetSessionMessagesAsync(session.SessionId).ConfigureAwait(true);
            string output = BuildTextTranscript(session, messages, markdown: true);
            await File.WriteAllTextAsync(dialog.FileName, output, Encoding.UTF8).ConfigureAwait(true);
            _viewModel.ConnectionStatus = $"Conversation exported: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            _viewModel.ConnectionStatus = $"Unable to export conversation: {ex.Message}";
        }
    }

    private async Task CopySessionTranscriptAsync(ChatSessionViewModel session)
    {
        try
        {
            IReadOnlyList<GeminiLiveShare.Core.Storage.ChatMessage> messages =
                await _viewModel.GetSessionMessagesAsync(session.SessionId).ConfigureAwait(true);
            string output = BuildTextTranscript(session, messages, markdown: true);
            System.Windows.Clipboard.SetText(output);
            _viewModel.ConnectionStatus = "Conversation copied to clipboard";
        }
        catch (Exception ex)
        {
            _viewModel.ConnectionStatus = $"Unable to copy conversation: {ex.Message}";
        }
    }

    private static string BuildExportFileName(string title)
    {
        string safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "conversation" : title);
        return $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.md";
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        string result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "conversation" : result;
    }

    private static string BuildTextTranscript(
        ChatSessionViewModel session,
        IReadOnlyList<GeminiLiveShare.Core.Storage.ChatMessage> messages,
        bool markdown)
    {
        StringBuilder builder = new();
        if (markdown)
        {
            builder.AppendLine($"# {session.Summary}");
            builder.AppendLine();
            builder.AppendLine($"- Session ID: `{session.SessionId}`");
            builder.AppendLine($"- Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine($"- Messages: {messages.Count}");
            builder.AppendLine();
            foreach (GeminiLiveShare.Core.Storage.ChatMessage message in messages.OrderBy(item => item.CreatedAtUtc))
            {
                string role = message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Gemini" : "User";
                DateTimeOffset local = new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAtUtc, DateTimeKind.Utc), TimeSpan.Zero).ToLocalTime();
                builder.AppendLine($"## {role} � {local:yyyy-MM-dd HH:mm:ss}");
                builder.AppendLine();
                builder.AppendLine(message.Text);
                builder.AppendLine();
            }
        }
        else
        {
            builder.AppendLine(session.Summary);
            builder.AppendLine(new string('=', session.Summary.Length));
            builder.AppendLine($"Session ID: {session.SessionId}");
            builder.AppendLine($"Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine($"Messages: {messages.Count}");
            builder.AppendLine();
            foreach (GeminiLiveShare.Core.Storage.ChatMessage message in messages.OrderBy(item => item.CreatedAtUtc))
            {
                string role = message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Gemini" : "User";
                DateTimeOffset local = new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAtUtc, DateTimeKind.Utc), TimeSpan.Zero).ToLocalTime();
                builder.AppendLine($"[{local:yyyy-MM-dd HH:mm:ss}] {role}");
                builder.AppendLine(message.Text);
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string BuildJsonTranscript(
        ChatSessionViewModel session,
        IReadOnlyList<GeminiLiveShare.Core.Storage.ChatMessage> messages)
    {
        var payload = new
        {
            sessionId = session.SessionId,
            title = session.Summary,
            exportedAt = DateTimeOffset.Now,
            messageCount = messages.Count,
            messages = messages
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => new
                {
                    id = item.Id,
                    role = item.Role,
                    createdAtUtc = DateTime.SpecifyKind(item.CreatedAtUtc, DateTimeKind.Utc),
                    text = item.Text
                })
                .ToArray()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
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
    #if DEBUG
        if (e.Key == Key.F12 && !_isCapturingHotkey)
        {
            e.Handled = true;
            BrowserAgentDebugPanel debugPanel = new(_browserAgentBridge)
            {
                Owner = this
            };
            debugPanel.Show();
            return;
        }
#endif

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

        // Safety guard: if the window ever closes without the explicit tray Exit command,
        // shut down the process so no hidden orphan instance keeps terminals blocked.
        if (!_isExiting)
        {
            _isExiting = true;
            System.Windows.Application.Current.Shutdown();
        }
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

