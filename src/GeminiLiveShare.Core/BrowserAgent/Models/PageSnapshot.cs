using System.Text.Json.Serialization;

namespace GeminiLiveShare.Core.BrowserAgent.Models;

public sealed class PageSnapshot
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<FormField> Fields { get; init; } = [];

    [JsonPropertyName("notices")]
    public IReadOnlyList<string> Notices { get; init; } = [];
}
