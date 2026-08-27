namespace GeminiLiveShare.Core.Storage;

public interface IChatHistoryRepository : IAsyncDisposable
{
    Task AddAsync(ChatMessage message);

    Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(string sessionId);
}