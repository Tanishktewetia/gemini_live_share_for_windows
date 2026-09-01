using Windows.Security.Credentials;

namespace GeminiLiveShare.Core.Security;

public sealed class ApiKeyVaultService : IApiKeyVaultService
{
    private const string ResourceName = "GeminiLiveShare.GeminiApiKey";
    private const string UserName = "GeminiApiKey";

    private readonly PasswordVault _vault = new();

    public string? GetApiKey()
    {
        try
        {
            PasswordCredential credential = _vault.Retrieve(ResourceName, UserName);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch
        {
            return null;
        }
    }

    public void SaveApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        try
        {
            PasswordCredential existing = _vault.Retrieve(ResourceName, UserName);
            _vault.Remove(existing);
        }
        catch
        {
            // The credential does not exist yet.
        }

        _vault.Add(new PasswordCredential(ResourceName, UserName, apiKey.Trim()));
    }

    public void DeleteApiKey()
    {
        try
        {
            PasswordCredential existing = _vault.Retrieve(ResourceName, UserName);
            _vault.Remove(existing);
        }
        catch
        {
            // Deleting an already absent key is intentionally idempotent.
        }
    }
}
