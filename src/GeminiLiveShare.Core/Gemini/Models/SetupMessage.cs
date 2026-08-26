using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.Gemini.Models;

public sealed class SetupMessage
{
    [JsonPropertyName("setup")]
    public required SetupConfiguration Setup { get; init; }
}

public sealed class SetupConfiguration
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("generationConfig")]
    public required AudioGenerationConfiguration GenerationConfig { get; init; }
}

public sealed class AudioGenerationConfiguration
{
    [JsonPropertyName("responseModalities")]
    public string[] ResponseModalities { get; init; } = ["AUDIO"];
}