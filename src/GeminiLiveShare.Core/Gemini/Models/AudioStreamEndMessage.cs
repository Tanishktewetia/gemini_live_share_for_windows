using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.Gemini.Models;

public sealed class AudioStreamEndMessage
{
    [JsonPropertyName("realtimeInput")]
    public AudioStreamEndInput RealtimeInput { get; init; } = new();
}

public sealed class AudioStreamEndInput
{
    [JsonPropertyName("audioStreamEnd")]
    public bool AudioStreamEnd { get; init; } = true;
}