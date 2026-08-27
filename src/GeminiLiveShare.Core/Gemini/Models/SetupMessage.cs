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

    [JsonPropertyName("inputAudioTranscription")]
    public AudioTranscriptionConfiguration InputAudioTranscription { get; init; } = new();

    [JsonPropertyName("outputAudioTranscription")]
    public AudioTranscriptionConfiguration OutputAudioTranscription { get; init; } = new();

    [JsonPropertyName("sessionResumption")]
    public SessionResumptionConfiguration SessionResumption { get; init; } = new();
}

public sealed class AudioTranscriptionConfiguration;

public sealed class SessionResumptionConfiguration
{
    [JsonPropertyName("handle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Handle { get; init; }
}

public sealed class AudioGenerationConfiguration
{
    [JsonPropertyName("responseModalities")]
    public string[] ResponseModalities { get; init; } = ["AUDIO"];
}