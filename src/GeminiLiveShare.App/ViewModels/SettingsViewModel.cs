using CommunityToolkit.Mvvm.ComponentModel;
using GeminiLiveShare.Core.Interop;
using GeminiLiveShare.Core.Security;

namespace GeminiLiveShare.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly ISensitiveContentFilterSettings _filterSettings;
    private readonly OverlayAppearanceSettings _overlaySettings;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSensitiveContentFilteringEnabled;

    [ObservableProperty]
    private bool _isOverlayDark;

    public SettingsViewModel(
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings,
        OverlayAppearanceSettings? overlaySettings = null)
    {
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _overlaySettings = overlaySettings ?? new OverlayAppearanceSettings();
        _isSensitiveContentFilteringEnabled = filterSettings.IsEnabled;
        _isOverlayDark = _overlaySettings.Theme == OverlayTheme.Dark;
    }

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
