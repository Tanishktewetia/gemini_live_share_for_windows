using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.BrowserAgent.Models;

public sealed class FormField
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}
