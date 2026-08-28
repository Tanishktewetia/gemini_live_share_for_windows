using SQLite;

namespace GeminiLiveShare.Core.Storage;

public sealed class ChatHistoryRepository : IChatHistoryRepository
{
    private readonly SQLiteAsyncConnection _connection;
    private readonly Task _initialization;

    public event EventHandler<ChatMessageAddedEventArgs>? MessageAdded;

    public ChatHistoryRepository(string? databasePath = null)
    {
        string path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeminiLiveShare",
            "chat-history.db3");
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SQLiteAsyncConnection(path);
        _initialization = _connection.CreateTableAsync<ChatMessage>();
    }

    public async Task AddAsync(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _initialization.ConfigureAwait(false);
        await _connection.InsertAsync(message).ConfigureAwait(false);
        MessageAdded?.Invoke(this, new ChatMessageAddedEventArgs(message));
    }

    public async Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await _initialization.ConfigureAwait(false);
        return await _connection.Table<ChatMessage>()
            .Where(message => message.SessionId == sessionId)
            .OrderBy(message => message.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetAllAsync()
    {
        await _initialization.ConfigureAwait(false);
        return await _connection.Table<ChatMessage>()
            .OrderByDescending(message => message.CreatedAtUtc)
            .ThenByDescending(message => message.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await _initialization.ConfigureAwait(false);
        await _connection.Table<ChatMessage>()
            .Where(message => message.SessionId == sessionId)
            .DeleteAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _initialization.ConfigureAwait(false);
        await _connection.CloseAsync().ConfigureAwait(false);
    }
}