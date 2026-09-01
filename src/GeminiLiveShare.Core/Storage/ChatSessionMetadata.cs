using SQLite;

namespace GeminiLiveShare.Core.Storage;

public sealed class ChatSessionMetadata
{
    [PrimaryKey]
    public string SessionId { get; set; } = string.Empty;

    public string Title { get; set; } = "New conversation";

    public bool IsTitleUserEdited { get; set; }
}
