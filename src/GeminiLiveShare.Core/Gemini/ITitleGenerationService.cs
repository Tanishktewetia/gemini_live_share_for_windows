using GeminiLiveShare.Core.Storage;

namespace GeminiLiveShare.Core.Gemini;

public interface ITitleGenerationService
{
    Task<string?> GenerateAsync(IReadOnlyList<ChatMessage> messages, string apiKey, CancellationToken cancellationToken = default);
}
