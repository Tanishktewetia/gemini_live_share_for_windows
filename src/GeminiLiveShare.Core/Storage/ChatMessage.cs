using SQLite;

namespace GeminiLiveShare.Core.Storage;

public sealed class ChatMessage
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string SessionId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    [Indexed]
    public DateTime CreatedAtUtc { get; set; }
}