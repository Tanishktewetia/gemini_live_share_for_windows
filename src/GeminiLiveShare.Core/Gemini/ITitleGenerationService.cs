namespace GeminiLiveShare.Core.Gemini;

public interface ITitleGenerationService
{
    Task<string?> GenerateAsync(string firstUserMessage, string apiKey, CancellationToken cancellationToken = default);
}
