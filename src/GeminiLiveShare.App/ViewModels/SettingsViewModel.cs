using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using GeminiLiveShare.Core.Diagnostics;
using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.Interop;
using GeminiLiveShare.Core.Security;

namespace GeminiLiveShare.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly ISensitiveContentFilterSettings _filterSettings;
    private readonly IDiagnosticsDebugSettings _diagnosticsSettings;
    private readonly OverlayAppearanceSettings _overlaySettings;
    private readonly HighlightSettings _highlightSettings;
    private readonly IAudioCaptureService _audioCapture;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSensitiveContentFilteringEnabled;

    [ObservableProperty]
    private bool _saveSentFramesForDiagnostics;

    [ObservableProperty]
    private bool _isOverlayDark;

    [ObservableProperty]
    private bool _isHighlightingEnabled;

    [ObservableProperty]
    private bool _showHighlightArrow;

    [ObservableProperty]
    private int _selectedInputDeviceNumber;

    public SettingsViewModel(
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings,
        IDiagnosticsDebugSettings diagnosticsSettings,
        OverlayAppearanceSettings? overlaySettings = null,
        HighlightSettings? highlightSettings = null,
        IAudioCaptureService? audioCapture = null)
    {
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _diagnosticsSettings = diagnosticsSettings;
        _overlaySettings = overlaySettings ?? new OverlayAppearanceSettings();
        _highlightSettings = highlightSettings ?? new HighlightSettings();
        _audioCapture = audioCapture ?? new AudioCaptureService();
        _isSensitiveContentFilteringEnabled = filterSettings.IsEnabled;
        _saveSentFramesForDiagnostics = diagnosticsSettings.SaveSentFrames;
        _isOverlayDark = _overlaySettings.Theme == OverlayTheme.Dark;
        _isHighlightingEnabled = _highlightSettings.IsEnabled;
        _showHighlightArrow = _highlightSettings.ShowArrow;
        _selectedInputDeviceNumber = _audioCapture.SelectedInputDeviceNumber;
    }

    public IReadOnlyList<AudioInputDeviceInfo> InputDevices => _audioCapture.InputDevices;

    public bool HasSavedApiKey => !string.IsNullOrWhiteSpace(_apiKeyVault.GetApiKey());

    public string ApiKeySummary
    {
        get
        {
            string? key = _apiKeyVault.GetApiKey();
            return string.IsNullOrWhiteSpace(key)
                ? "No API key configured"
                : $"••••••••{key[^Math.Min(4, key.Length)..]}";
        }
    }

    public string OverlayPositionSummary => _overlaySettings.Position switch
    {
        OverlayPosition.BottomCenter => "Bottom center",
        OverlayPosition.Custom => "Custom",
        _ => "Top center"
    };

    public string SentFramesDirectory => _diagnosticsSettings.SentFramesDirectory;

    public string LogsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeminiLiveShare",
        "logs");

    public string TodaySessionLogPath => Path.Combine(LogsDirectory, $"session-{DateTime.Now:yyyyMMdd}.log");

    partial void OnIsSensitiveContentFilteringEnabledChanged(bool value)
    {
        try
        {
            _filterSettings.IsEnabled = value;
        }
        catch (Exception ex)
        {
            _isSensitiveContentFilteringEnabled = _filterSettings.IsEnabled;
            OnPropertyChanged(nameof(IsSensitiveContentFilteringEnabled));
            StatusMessage = $"Could not save the filtering setting: {ex.Message}";
        }
    }

    partial void OnSaveSentFramesForDiagnosticsChanged(bool value)
    {
        try
        {
            _diagnosticsSettings.SaveSentFrames = value;
            StatusMessage = value
                ? "Debug frame capture enabled. Sanitized JPEGs will be saved while screen sharing is on."
                : "Debug frame capture disabled.";
        }
        catch (Exception ex)
        {
            _saveSentFramesForDiagnostics = _diagnosticsSettings.SaveSentFrames;
            OnPropertyChanged(nameof(SaveSentFramesForDiagnostics));
            StatusMessage = $"Could not save diagnostics setting: {ex.Message}";
        }
    }

    partial void OnSelectedInputDeviceNumberChanged(int value) => _audioCapture.SelectedInputDeviceNumber = value;

    partial void OnIsHighlightingEnabledChanged(bool value) => _highlightSettings.IsEnabled = value;

    partial void OnShowHighlightArrowChanged(bool value) => _highlightSettings.ShowArrow = value;

    partial void OnIsOverlayDarkChanged(bool value)
    {
        _overlaySettings.Theme = value ? OverlayTheme.Dark : OverlayTheme.Light;
    }

    public bool Save(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusMessage = "Enter an API key before saving.";
            return false;
        }

        try
        {
            _apiKeyVault.SaveApiKey(apiKey);
            StatusMessage = "API key saved securely in Windows Credential Locker.";
            OnPropertyChanged(nameof(HasSavedApiKey));
            OnPropertyChanged(nameof(ApiKeySummary));
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save the API key: {ex.Message}";
            return false;
        }
    }

    public bool DeleteApiKey()
    {
        try
        {
            _apiKeyVault.DeleteApiKey();
            StatusMessage = "API key deleted from Windows Credential Locker.";
            OnPropertyChanged(nameof(HasSavedApiKey));
            OnPropertyChanged(nameof(ApiKeySummary));
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete the API key: {ex.Message}";
            return false;
        }
    }

    public void ResetOverlayPosition()
    {
        _overlaySettings.ResetPosition();
        OnPropertyChanged(nameof(OverlayPositionSummary));
    }
}


