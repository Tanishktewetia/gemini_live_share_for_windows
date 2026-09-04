using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.BrowserAgent.Models;

public sealed class ToolCallResult
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_result";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}