namespace GeminiLiveShare.Core.Gemini;

public sealed class TranscriptionEventArgs(string role, string text) : EventArgs
{
    public string Role { get; } = role;

    public string Text { get; } = text;
}

public sealed class ConnectionAvailabilityChangedEventArgs(bool isAvailable) : EventArgs
{
    public bool IsAvailable { get; } = isAvailable;
}