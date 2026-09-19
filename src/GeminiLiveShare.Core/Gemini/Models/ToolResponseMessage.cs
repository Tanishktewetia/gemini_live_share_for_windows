using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.Gemini.Models;

public sealed class ToolResponseMessage
{
    [JsonPropertyName("toolResponse")]
    public required ToolResponsePayloadContent ToolResponse { get; init; }
}

public sealed class ToolResponsePayloadContent
{
    [JsonPropertyName("functionResponses")]
    public required FunctionResponse[] FunctionResponses { get; init; }
}

public sealed class FunctionResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("response")]
    public required JsonElement Response { get; init; }
}
