namespace GeminiLiveShare.Core.Storage;

public interface IChatHistoryRepository : IAsyncDisposable
{
    event EventHandler<ChatMessageAddedEventArgs>? MessageAdded;

    Task AddAsync(ChatMessage message);

    Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(string sessionId);

    Task<IReadOnlyList<ChatMessage>> GetAllAsync();

    Task<IReadOnlyList<ChatSessionMetadata>> GetSessionMetadataAsync();

    Task SetSessionTitleAsync(string sessionId, string title, bool isUserEdited);

    Task DeleteSessionAsync(string sessionId);
}

public sealed class ChatMessageAddedEventArgs(ChatMessage message) : EventArgs
{
    public ChatMessage Message { get; } = message;
}
