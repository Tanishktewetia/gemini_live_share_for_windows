using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.Core.Interop;

namespace GeminiLiveShare.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Func<GlobalHotkeyConfiguration, bool>? _tryUpdateHotkey;
    private GlobalHotkeyConfiguration _hotkeyConfiguration;
    private bool _isCapturingHotkey;

    public SettingsWindow(
        SettingsViewModel viewModel,
        Func<GlobalHotkeyConfiguration, bool>? tryUpdateHotkey = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _tryUpdateHotkey = tryUpdateHotkey;
        _hotkeyConfiguration = new GlobalHotkeySettings().Load();
        DataContext = viewModel;
        UpdateThemeButtons();
        UpdateHotkeyDisplay();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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
        if (_viewModel.Save(ApiKeyInput.Password))
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

    private void OnDeleteKeyClick(object sender, RoutedEventArgs e)
    {
        DeleteConfirmationPopup.IsOpen = true;
    }

    private void OnCancelDeleteClick(object sender, RoutedEventArgs e)
    {
        DeleteConfirmationPopup.IsOpen = false;
    }

    private void OnConfirmDeleteClick(object sender, RoutedEventArgs e)
    {
        DeleteConfirmationPopup.IsOpen = false;
        _viewModel.DeleteApiKey();
    }

    private void OnLightThemeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsOverlayDark = false;
        UpdateThemeButtons();
    }

    private void OnDarkThemeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsOverlayDark = true;
        UpdateThemeButtons();
    }

    private void UpdateThemeButtons()
    {
        LightThemeButton.IsChecked = !_viewModel.IsOverlayDark;
        DarkThemeButton.IsChecked = _viewModel.IsOverlayDark;
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetOverlayPosition();
        _viewModel.StatusMessage = "Overlay position reset to top center.";
    }

    private void OnChangeHotkeyClick(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        ChangeHotkeyButton.Content = "Press new key combination...";
        HotkeyError.Text = string.Empty;
        HotkeyError.Visibility = Visibility.Collapsed;
        Keyboard.Focus(ChangeHotkeyButton);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
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
        if (_tryUpdateHotkey is not null && !_tryUpdateHotkey(configuration))
        {
            ShowHotkeyError("Unable to register that key combination");
            return;
        }

        try
        {
            new GlobalHotkeySettings().Save(configuration);
            _hotkeyConfiguration = configuration;
            _viewModel.StatusMessage = "Global hotkey updated.";
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
        if ((_hotkeyConfiguration.Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((_hotkeyConfiguration.Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((_hotkeyConfiguration.Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((_hotkeyConfiguration.Modifiers & HotkeyModifiers.Windows) != 0) parts.Add("Win");
        Key key = KeyInterop.KeyFromVirtualKey((int)_hotkeyConfiguration.VirtualKey);
        parts.Add(GetKeyDisplayName(key));
        HotkeyDisplay.Text = string.Join(" + ", parts);
    }

    private static string GetKeyDisplayName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        return key.ToString();
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

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
}
