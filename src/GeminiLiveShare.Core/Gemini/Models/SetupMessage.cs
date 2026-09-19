using System.Text.Json;
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

    [JsonPropertyName("systemInstruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstructionContent? SystemInstruction { get; init; }

    [JsonPropertyName("inputAudioTranscription")]
    public AudioTranscriptionConfiguration InputAudioTranscription { get; init; } = new();

    [JsonPropertyName("outputAudioTranscription")]
    public AudioTranscriptionConfiguration OutputAudioTranscription { get; init; } = new();

    [JsonPropertyName("sessionResumption")]
    public SessionResumptionConfiguration SessionResumption { get; init; } = new();

    // Google Search grounding runs on Google's side. Null omits tools (keys without search quota reject the setup).
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolConfiguration[]? Tools { get; init; }
}

public sealed class ToolConfiguration
{
    [JsonPropertyName("googleSearch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleSearchTool? GoogleSearch { get; init; }

    [JsonPropertyName("functionDeclarations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionDeclaration[]? FunctionDeclarations { get; init; }
}

public sealed class GoogleSearchTool;

public sealed class FunctionDeclaration
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; init; }
}

public sealed class AudioTranscriptionConfiguration;

public sealed class InstructionContent
{
    [JsonPropertyName("parts")]
    public required InstructionPart[] Parts { get; init; }
}

public sealed class InstructionPart
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

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

    // Desktop screenshots contain small text and icons. High media resolution enables
    // Gemini's higher-detail visual processing for the realtime video stream.
    [JsonPropertyName("mediaResolution")]
    public string MediaResolution { get; init; } = "MEDIA_RESOLUTION_HIGH";
}
