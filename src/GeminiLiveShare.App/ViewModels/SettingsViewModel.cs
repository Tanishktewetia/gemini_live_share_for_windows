using CommunityToolkit.Mvvm.ComponentModel;
using GeminiLiveShare.Core.Security;

namespace GeminiLiveShare.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IApiKeyVaultService _apiKeyVault;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public SettingsViewModel(IApiKeyVaultService apiKeyVault)
    {
        _apiKeyVault = apiKeyVault;
    }

    public bool HasSavedApiKey => !string.IsNullOrWhiteSpace(_apiKeyVault.GetApiKey());

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