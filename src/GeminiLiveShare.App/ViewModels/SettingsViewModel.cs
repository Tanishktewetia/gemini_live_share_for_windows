using CommunityToolkit.Mvvm.ComponentModel;
using GeminiLiveShare.Core.Security;

namespace GeminiLiveShare.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly ISensitiveContentFilterSettings _filterSettings;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSensitiveContentFilteringEnabled;

    public SettingsViewModel(
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings)
    {
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _isSensitiveContentFilteringEnabled = filterSettings.IsEnabled;
    }

    public bool HasSavedApiKey => !string.IsNullOrWhiteSpace(_apiKeyVault.GetApiKey());

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
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save the API key: {ex.Message}";
            return false;
        }
    }
}