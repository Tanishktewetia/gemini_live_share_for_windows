namespace GeminiLiveShare.Core.Gemini.Models;

public sealed record ServerContentMessage(
    IReadOnlyList<byte[]> AudioChunks,
    bool Interrupted,
    bool TurnComplete);