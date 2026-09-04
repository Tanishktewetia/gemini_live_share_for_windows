using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.BrowserAgent.Models;

public sealed class ToolCallRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_call";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("payload")]
    public required ToolCallPayload Payload { get; init; }
}

public sealed class ToolCallPayload
{
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("args")]
    public JsonElement Args { get; init; }
}