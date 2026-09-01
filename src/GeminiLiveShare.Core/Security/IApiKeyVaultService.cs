namespace GeminiLiveShare.Core.Security;

public interface IApiKeyVaultService
{
    string? GetApiKey();

    void SaveApiKey(string apiKey);

    void DeleteApiKey();
}
