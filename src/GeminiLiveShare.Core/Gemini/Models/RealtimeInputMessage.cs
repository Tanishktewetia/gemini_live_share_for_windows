using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.Gemini.Models;

public sealed class RealtimeInputMessage
{
    [JsonPropertyName("realtimeInput")]
    public required RealtimeInput RealtimeInput { get; init; }
}

public sealed class RealtimeInput
{
    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AudioBlob? Audio { get; init; }

    [JsonPropertyName("video")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoBlob? Video { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
}

public sealed class AudioBlob
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = "audio/pcm;rate=16000";

    [JsonPropertyName("data")]
    public required string Data { get; init; }
}

public sealed class VideoBlob
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = "image/png";

    [JsonPropertyName("data")]
    public required string Data { get; init; }
}
